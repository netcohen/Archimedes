import { chromium, Browser, Page, BrowserContext } from "playwright";
import * as crypto from "crypto";
import * as fs from "fs";
import * as path from "path";
import { safeLog } from "./redactor";

export type BrowserAction =
  | "openUrl"
  | "click"
  | "fill"
  | "waitFor"
  | "extractTable"
  | "downloadFile"
  | "screenshotSelector"
  | "detectLoginForm";

export interface BrowserStep {
  action: BrowserAction;
  params: Record<string, unknown>;
}

export interface BrowserStepResult {
  success: boolean;
  action: string;
  durationMs: number;
  data?: unknown;
  error?: string;
}

export interface BrowserRunStatus {
  runId: string;
  status: "idle" | "running" | "completed" | "failed";
  currentStep?: number;
  totalSteps?: number;
  results: BrowserStepResult[];
  error?: string;
}

const runs: Map<string, BrowserRunStatus> = new Map();
let browser: Browser | null = null;
let browserAvailable = true;

async function ensureBrowser(): Promise<Browser> {
  if (browser && browser.isConnected()) {
    return browser;
  }

  try {
    browser = await chromium.launch({
      headless: false,
      args: ["--disable-blink-features=AutomationControlled"],
    });
    browserAvailable = true;
    safeLog("Browser", "Chromium browser launched");
    return browser;
  } catch (err: unknown) {
    browserAvailable = false;
    const msg = err instanceof Error ? err.message : String(err);
    safeLog("Browser", `Failed to launch browser: ${msg}`);
    throw new Error(`Browser unavailable: ${msg}`);
  }
}

export async function isBrowserAvailable(): Promise<boolean> {
  try {
    const b = await ensureBrowser();
    return b.isConnected();
  } catch {
    return false;
  }
}

async function executeStep(
  page: Page,
  step: BrowserStep,
  downloadDir: string
): Promise<BrowserStepResult> {
  const start = Date.now();
  const result: BrowserStepResult = {
    success: false,
    action: step.action,
    durationMs: 0,
  };

  try {
    switch (step.action) {
      case "openUrl": {
        const url = step.params.url as string;
        await page.goto(url, { waitUntil: "domcontentloaded" });
        safeLog("Browser", `Opened URL (hash: ${hashString(url)})`);
        result.success = true;
        break;
      }

      case "click": {
        const selector = step.params.selector as string;
        await page.click(selector, { timeout: 10000 });
        safeLog("Browser", `Clicked: ${selector}`);
        result.success = true;
        break;
      }

      case "fill": {
        const selector = step.params.selector as string;
        const value = step.params.value as string;
        await page.fill(selector, value);
        safeLog("Browser", `Filled: ${selector} (len=${value.length})`);
        result.success = true;
        break;
      }

      case "waitFor": {
        const selector = step.params.selector as string;
        const timeout = (step.params.timeout as number) || 10000;
        await page.waitForSelector(selector, { timeout });
        safeLog("Browser", `WaitFor: ${selector}`);
        result.success = true;
        break;
      }

      case "extractTable": {
        const selector = step.params.selector as string;
        const data = await page.evaluate((sel) => {
          const table = document.querySelector(sel);
          if (!table) return null;
          const rows = Array.from(table.querySelectorAll("tr"));
          return rows.map((row: Element) =>
            Array.from(row.querySelectorAll("th, td")).map(
              (cell: Element) => cell.textContent?.trim() || ""
            )
          );
        }, selector);
        result.data = data;
        result.success = true;
        safeLog("Browser", `ExtractTable: ${selector} (${(data as string[][] | null)?.length || 0} rows)`);
        break;
      }

      case "downloadFile": {
        const linkSelector = step.params.selector as string;
        const filename = (step.params.filename as string) || "download";

        const [download] = await Promise.all([
          page.waitForEvent("download"),
          page.click(linkSelector),
        ]);

        const filePath = path.join(downloadDir, filename);
        await download.saveAs(filePath);
        result.data = { path: filePath, suggestedFilename: download.suggestedFilename() };
        result.success = true;
        safeLog("Browser", `Downloaded: ${filename}`);
        break;
      }

      case "screenshotSelector": {
        const selector = step.params.selector as string;
        const filename = (step.params.filename as string) || `screenshot-${Date.now()}.png`;
        const element = page.locator(selector);
        const screenshotPath = path.join(downloadDir, filename);
        await element.screenshot({ path: screenshotPath });
        result.data = { path: screenshotPath };
        result.success = true;
        safeLog("Browser", `Screenshot: ${selector} -> ${filename}`);
        break;
      }

      case "detectLoginForm": {
        const data = await page.evaluate(() => {
          const inputs = Array.from(document.querySelectorAll("input"));
          const passwordInput = inputs.find((i) => i.type === "password");
          const usernameInput = inputs.find(
            (i) =>
              i.type === "text" ||
              i.type === "email" ||
              /user|email|login/i.test(i.name || i.id)
          );
          const submitBtn = document.querySelector<HTMLElement>(
            'button[type="submit"], input[type="submit"]'
          );
          const usernameSelector = usernameInput
            ? usernameInput.id
              ? `#${usernameInput.id}`
              : `[name="${usernameInput.name}"]`
            : null;
          const passwordSelector = passwordInput
            ? passwordInput.id
              ? `#${passwordInput.id}`
              : `[name="${passwordInput.name}"]`
            : null;
          const submitSelector = submitBtn
            ? submitBtn.id
              ? `#${submitBtn.id}`
              : 'button[type="submit"]'
            : null;
          return {
            found: !!passwordInput,
            selectors: { username: usernameSelector, password: passwordSelector, submit: submitSelector },
          };
        });
        result.data = data;
        result.success = true;
        safeLog("Browser", `DetectLoginForm: found=${(data as { found: boolean }).found}`);
        break;
      }

      default:
        result.error = `Unknown action: ${step.action}`;
    }
  } catch (err: unknown) {
    result.error = err instanceof Error ? err.message : String(err);
    safeLog("Browser", `Step failed: ${step.action} - ${result.error}`);
  }

  result.durationMs = Date.now() - start;
  return result;
}

export async function runBrowserSteps(
  steps: BrowserStep[],
  runId?: string
): Promise<BrowserRunStatus> {
  const id = runId || crypto.randomBytes(8).toString("hex");
  const downloadDir = path.join(
    process.env.TEMP || "/tmp",
    "archimedes-downloads",
    id
  );
  fs.mkdirSync(downloadDir, { recursive: true });

  const status: BrowserRunStatus = {
    runId: id,
    status: "running",
    currentStep: 0,
    totalSteps: steps.length,
    results: [],
  };
  runs.set(id, status);

  let page: Page | null = null;
  let context: BrowserContext | null = null;

  try {
    const b = await ensureBrowser();
    context = await b.newContext({
      acceptDownloads: true,
    });
    page = await context.newPage();

    for (let i = 0; i < steps.length; i++) {
      status.currentStep = i + 1;
      const result = await executeStep(page, steps[i], downloadDir);
      status.results.push(result);

      if (!result.success) {
        status.status = "failed";
        status.error = result.error;
        break;
      }
    }

    if (status.status === "running") {
      status.status = "completed";
    }
  } catch (err: unknown) {
    status.status = "failed";
    status.error = err instanceof Error ? err.message : String(err);
  } finally {
    if (page) await page.close().catch(() => {});
    if (context) await context.close().catch(() => {});
  }

  runs.set(id, status);
  return status;
}

export function getRunStatus(runId: string): BrowserRunStatus | null {
  return runs.get(runId) || null;
}

export function getAllRuns(): BrowserRunStatus[] {
  return Array.from(runs.values());
}

function hashString(s: string): string {
  return crypto.createHash("sha256").update(s).digest("hex").slice(0, 12);
}
