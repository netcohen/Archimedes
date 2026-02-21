import * as http from "http";
import * as url from "url";
import * as crypto from "crypto";

const loginHtml = `<!DOCTYPE html>
<html>
<head><title>Testsite Login</title></head>
<body>
  <h1>Testsite Login</h1>
  <form id="loginForm" action="/testsite/dashboard" method="GET">
    <input type="text" id="username" name="username" placeholder="Username" required>
    <input type="password" id="password" name="password" placeholder="Password" required>
    <button type="submit" id="loginBtn">Login</button>
  </form>
</body>
</html>`;

const dashboardHtml = `<!DOCTYPE html>
<html>
<head><title>Dashboard</title></head>
<body>
  <h1>Dashboard</h1>
  <table id="dataTable">
    <thead>
      <tr><th>ID</th><th>Name</th><th>Status</th><th>Value</th></tr>
    </thead>
    <tbody>
      <tr><td>1</td><td>Alpha</td><td>Active</td><td>100</td></tr>
      <tr><td>2</td><td>Beta</td><td>Pending</td><td>250</td></tr>
      <tr><td>3</td><td>Gamma</td><td>Active</td><td>175</td></tr>
      <tr><td>4</td><td>Delta</td><td>Inactive</td><td>50</td></tr>
    </tbody>
  </table>
  <a href="/testsite/download" id="downloadLink">Download CSV</a>
</body>
</html>`;

function generateCaptchaCode(): string {
  return crypto.randomBytes(3).toString("hex").toUpperCase();
}

let currentCaptcha = generateCaptchaCode();

const captchaHtml = () => `<!DOCTYPE html>
<html>
<head><title>Captcha Verification</title></head>
<body>
  <h1>Human Verification</h1>
  <div id="captchaImage" style="font-family: monospace; font-size: 24px; background: #eee; padding: 20px; letter-spacing: 8px;">
    ${currentCaptcha}
  </div>
  <form id="captchaForm" method="POST" action="/testsite/captcha/verify">
    <input type="text" id="captchaInput" name="captcha" placeholder="Enter code above" required>
    <button type="submit" id="verifyBtn">Verify</button>
  </form>
</body>
</html>`;

const csvData = `ID,Name,Status,Value
1,Alpha,Active,100
2,Beta,Pending,250
3,Gamma,Active,175
4,Delta,Inactive,50`;

export function handleTestsite(
  req: http.IncomingMessage,
  res: http.ServerResponse
): boolean {
  const parsedUrl = url.parse(req.url || "", true);
  const path = parsedUrl.pathname || "";

  if (!path.startsWith("/testsite")) {
    return false;
  }

  if (path === "/testsite/login" || path === "/testsite") {
    res.writeHead(200, { "Content-Type": "text/html" });
    res.end(loginHtml);
    return true;
  }

  if (path === "/testsite/dashboard") {
    res.writeHead(200, { "Content-Type": "text/html" });
    res.end(dashboardHtml);
    return true;
  }

  if (path === "/testsite/captcha") {
    currentCaptcha = generateCaptchaCode();
    res.writeHead(200, { "Content-Type": "text/html" });
    res.end(captchaHtml());
    return true;
  }

  if (path === "/testsite/captcha/verify" && req.method === "POST") {
    let body = "";
    req.on("data", (chunk) => (body += chunk));
    req.on("end", () => {
      const params = new URLSearchParams(body);
      const input = params.get("captcha")?.toUpperCase();
      if (input === currentCaptcha) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ success: true }));
      } else {
        res.writeHead(400, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ success: false, error: "Invalid captcha" }));
      }
    });
    return true;
  }

  if (path === "/testsite/captcha/current") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ code: currentCaptcha }));
    return true;
  }

  if (path === "/testsite/download") {
    res.writeHead(200, {
      "Content-Type": "text/csv",
      "Content-Disposition": 'attachment; filename="data.csv"',
    });
    res.end(csvData);
    return true;
  }

  res.writeHead(404);
  res.end("Not found");
  return true;
}
