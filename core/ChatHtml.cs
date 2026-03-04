namespace Archimedes.Core;

/// <summary>
/// Phase 22+24+27+28 – Chat UI: Self-contained HTML for the Archimedes chat interface.
/// Served at GET /chat. No external dependencies — vanilla HTML/CSS/JS only.
/// Features: RTL Hebrew, chat area, system metrics bar, tasks panel, status bar,
///           recovery dialogue cards (Phase 24), tool acquisition panel (Phase 27),
///           machine migration panel (Phase 28).
/// Version: v0.28.0
/// </summary>
public static class ChatHtml
{
    public static string Page => """
        <!DOCTYPE html>
        <html dir="rtl" lang="he">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Archimedes</title>
          <style>
            /* ── Reset & base ─────────────────────────────────────────── */
            *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

            html, body {
              height: 100%;
              width: 100%;
            }

            body {
              font-family: 'Segoe UI', Arial, sans-serif;
              background: #0d1117;
              color: #e6edf3;
              display: flex;
              flex-direction: column;
              overflow: hidden;
            }

            /* ── Top metrics bar ──────────────────────────────────────── */
            /* dir="rtl" already makes flex flow RIGHT→LEFT — no row-reverse needed */
            #topbar {
              background: #161b22;
              border-bottom: 1px solid #30363d;
              padding: 7px 16px;
              display: flex;
              align-items: center;
              gap: 20px;
              flex-shrink: 0;
              width: 100%;
            }
            /* Logo anchored to right (flex-start in RTL) */
            #topbar .logo {
              font-weight: 700;
              font-size: 1rem;
              color: #58a6ff;
              letter-spacing: 1px;
            }
            /* Version pushed to far left (flex-end in RTL) */
            #topbar .version {
              margin-inline-start: auto;
              font-size: .7rem;
              color: #484f58;
            }
            .metric {
              display: flex;
              align-items: center;
              gap: 5px;
              font-size: 0.8rem;
              color: #8b949e;
              white-space: nowrap;
            }
            .metric .val { color: #e6edf3; font-weight: 600; }
            #statusdot {
              width: 8px; height: 8px;
              border-radius: 50%;
              background: #3fb950;
              box-shadow: 0 0 5px #3fb950;
              flex-shrink: 0;
            }

            /* ── Main layout ──────────────────────────────────────────── */
            /* dir="rtl" + flex row: first child → RIGHT, second → LEFT   */
            #main {
              display: flex;
              flex: 1;
              overflow: hidden;
              width: 100%;
              min-height: 0;
            }

            /* ── Chat area (RIGHT side — first child in DOM) ──────────── */
            #chat-area {
              flex: 1;
              min-width: 0;
              display: flex;
              flex-direction: column;
              overflow: hidden;
            }
            #messages {
              flex: 1;
              overflow-y: auto;
              padding: 16px 20px;
              display: flex;
              flex-direction: column;
              gap: 10px;
              min-height: 0;
            }
            .msg {
              max-width: 72%;
              padding: 10px 14px;
              border-radius: 12px;
              font-size: 0.9rem;
              line-height: 1.55;
              word-break: break-word;
              white-space: pre-wrap;
            }
            /* User messages: right side (RTL flex-start) */
            .msg-user {
              background: #1f6feb;
              color: #fff;
              align-self: flex-start;
              border-bottom-right-radius: 3px;
            }
            /* System/Archimedes messages: left side */
            .msg-system {
              background: #161b22;
              color: #e6edf3;
              align-self: flex-end;
              border: 1px solid #30363d;
              border-bottom-left-radius: 3px;
            }
            .msg-system .intent-chip {
              display: inline-block;
              background: #388bfd18;
              color: #58a6ff;
              border: 1px solid #388bfd44;
              border-radius: 4px;
              font-size: 0.7rem;
              padding: 1px 7px;
              margin-bottom: 5px;
              letter-spacing: 0.5px;
              font-family: monospace;
            }

            /* ── Input bar ────────────────────────────────────────────── */
            /* Textarea is first in DOM → right side; button is second → left side */
            #input-bar {
              padding: 10px 16px;
              border-top: 1px solid #30363d;
              display: flex;
              gap: 8px;
              background: #161b22;
            }
            #msg-input {
              flex: 1;
              min-width: 0;
              background: #21262d;
              border: 1px solid #30363d;
              color: #e6edf3;
              border-radius: 8px;
              padding: 9px 14px;
              font-size: 0.92rem;
              outline: none;
              direction: rtl;
              font-family: inherit;
              resize: none;
              height: 40px;
              max-height: 120px;
              overflow-y: auto;
            }
            #msg-input:focus { border-color: #388bfd; }
            #msg-input::placeholder { color: #484f58; }
            #send-btn {
              background: #1f6feb;
              border: none;
              color: #fff;
              border-radius: 8px;
              width: 40px;
              cursor: pointer;
              font-size: 1.1rem;
              transition: background .15s;
              flex-shrink: 0;
            }
            #send-btn:hover    { background: #388bfd; }
            #send-btn:disabled { background: #21262d; color: #484f58; cursor: default; }

            /* ── Tasks panel (LEFT side — second child in DOM) ────────── */
            #tasks-panel {
              width: 240px;
              flex-shrink: 0;
              border-inline-start: 1px solid #30363d;
              display: flex;
              flex-direction: column;
              background: #0d1117;
            }
            #tasks-header {
              padding: 10px 12px;
              font-size: 0.75rem;
              font-weight: 700;
              color: #8b949e;
              text-transform: uppercase;
              letter-spacing: 0.5px;
              border-bottom: 1px solid #30363d;
            }
            #tasks-list {
              flex: 1;
              overflow-y: auto;
              padding: 8px;
              display: flex;
              flex-direction: column;
              gap: 6px;
              min-height: 0;
            }
            .task-item {
              background: #161b22;
              border: 1px solid #30363d;
              border-radius: 8px;
              padding: 8px 10px;
              font-size: 0.8rem;
            }
            .task-item .t-id    { color: #484f58; font-size: 0.7rem; margin-bottom: 2px; }
            .task-item .t-title { color: #e6edf3; margin-bottom: 4px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            .task-item .t-state { font-size: 0.7rem; padding: 1px 6px; border-radius: 4px; display: inline-block; }
            .s-running   { background: #1f6feb22; color: #58a6ff; border: 1px solid #1f6feb44; }
            .s-pending   { background: #d2992222; color: #d29922; border: 1px solid #d2992244; }
            .s-completed { background: #3fb95022; color: #3fb950; border: 1px solid #3fb95044; }
            .s-failed    { background: #f8514922; color: #f85149; border: 1px solid #f8514944; }
            .s-other     { background: #30363d; color: #8b949e; }
            #no-tasks    { color: #484f58; font-size: 0.8rem; text-align: center; padding: 24px 8px; }

            /* ── Status bar ───────────────────────────────────────────── */
            #statusbar {
              background: #161b22;
              border-top: 1px solid #21262d;
              padding: 4px 16px;
              font-size: 0.76rem;
              color: #8b949e;
              display: flex;
              align-items: center;
              gap: 7px;
              flex-shrink: 0;
              height: 26px;
              width: 100%;
            }
            .spin {
              display: none;
              width: 10px; height: 10px;
              border: 2px solid #30363d;
              border-top-color: #58a6ff;
              border-radius: 50%;
              animation: spin .7s linear infinite;
              flex-shrink: 0;
            }
            .spin.on { display: inline-block; }
            @keyframes spin { to { transform: rotate(360deg); } }

            /* ── Recovery dialogue cards (Phase 24) ───────────────────── */
            #recovery-area {
              background: #161b22;
              border-bottom: 1px solid #30363d;
              padding: 0;
              flex-shrink: 0;
              width: 100%;
              display: none;   /* hidden when no pending dialogues */
            }
            #recovery-area.has-items { display: block; }
            .recovery-card {
              display: flex;
              flex-direction: column;
              gap: 8px;
              padding: 10px 16px;
              border-bottom: 1px solid #21262d;
            }
            .recovery-card:last-child { border-bottom: none; }
            .recovery-header {
              display: flex;
              align-items: center;
              gap: 8px;
              font-size: 0.8rem;
              color: #f85149;
              font-weight: 600;
            }
            .recovery-task {
              font-size: 0.75rem;
              color: #8b949e;
            }
            .recovery-question {
              font-size: 0.88rem;
              color: #e6edf3;
              line-height: 1.5;
            }
            .recovery-actions {
              display: flex;
              gap: 8px;
            }
            .rec-btn {
              font-size: 0.78rem;
              padding: 4px 12px;
              border-radius: 6px;
              border: 1px solid;
              cursor: pointer;
              font-family: inherit;
              transition: opacity .15s;
            }
            .rec-btn:hover { opacity: 0.8; }
            .rec-btn-retry   { background: #1f6feb22; color: #58a6ff; border-color: #1f6feb44; }
            .rec-btn-dismiss { background: #f8514922; color: #f85149; border-color: #f8514944; }

            /* ── Goals section (Phase 26) ─────────────────────────────── */
            #goals-header {
              padding: 10px 12px 6px;
              font-size: 0.75rem;
              font-weight: 700;
              color: #8b949e;
              text-transform: uppercase;
              letter-spacing: 0.5px;
              border-top: 1px solid #30363d;
              border-bottom: 1px solid #21262d;
            }
            #goals-list {
              overflow-y: auto;
              padding: 6px 8px;
              display: flex;
              flex-direction: column;
              gap: 5px;
              max-height: 160px;
            }
            .goal-item {
              background: #161b22;
              border: 1px solid #30363d;
              border-radius: 8px;
              padding: 7px 10px;
              font-size: 0.78rem;
            }
            .goal-item .g-title { color: #e6edf3; margin-bottom: 3px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            .goal-item .g-meta  { display: flex; gap: 6px; align-items: center; }
            .goal-item .g-state { font-size: 0.68rem; padding: 1px 5px; border-radius: 4px; display: inline-block; }
            .gs-active    { background: #388bfd22; color: #58a6ff; border: 1px solid #388bfd44; }
            .gs-monitoring{ background: #d2992222; color: #d29922; border: 1px solid #d2992244; }
            .gs-completed { background: #3fb95022; color: #3fb950; border: 1px solid #3fb95044; }
            .gs-failed    { background: #f8514922; color: #f85149; border: 1px solid #f8514944; }
            .gs-idle      { background: #30363d; color: #8b949e; }
            .goal-item .g-progress {
              flex: 1; height: 3px; background: #21262d; border-radius: 2px; overflow: hidden;
            }
            .goal-item .g-progress-bar { height: 100%; background: #388bfd; border-radius: 2px; }
            #no-goals { color: #484f58; font-size: 0.78rem; text-align: center; padding: 12px 8px; }

            /* ── Tools section (Phase 27) ─────────────────────────────── */
            #tools-header, #gaps-header, #legal-header {
              padding: 8px 12px 4px;
              font-size: 0.72rem;
              font-weight: 700;
              color: #8b949e;
              text-transform: uppercase;
              letter-spacing: 0.5px;
              border-top: 1px solid #30363d;
            }
            #tools-list, #gaps-list, #legal-list {
              padding: 4px 8px;
              display: flex;
              flex-direction: column;
              gap: 4px;
              max-height: 120px;
              overflow-y: auto;
            }
            .tool-item {
              background: #161b22;
              border: 1px solid #30363d;
              border-radius: 6px;
              padding: 5px 8px;
              font-size: 0.74rem;
              display: flex;
              align-items: center;
              gap: 6px;
            }
            .tool-cap  { color: #58a6ff; font-weight: 600; }
            .tool-name { color: #8b949e; flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            .tool-risk-safe { color: #3fb950; }
            .tool-risk-mgmt { color: #d29922; }
            .tool-risk-dang { color: #f85149; }
            .gap-item {
              background: #21262d;
              border: 1px solid #d2992244;
              border-radius: 6px;
              padding: 5px 8px;
              font-size: 0.73rem;
              color: #d29922;
            }
            .legal-item {
              background: #21262d;
              border: 1px solid #388bfd44;
              border-radius: 6px;
              padding: 5px 8px;
              font-size: 0.73rem;
              color: #e6edf3;
            }
            .legal-btn {
              font-size: 0.68rem;
              padding: 2px 7px;
              border-radius: 4px;
              border: none;
              cursor: pointer;
              margin-right: 4px;
            }
            .legal-btn-approve { background: #3fb95022; color: #3fb950; border: 1px solid #3fb95044; }
            .legal-btn-reject  { background: #f8514922; color: #f85149; border: 1px solid #f8514944; }

            /* ── Scrollbar ────────────────────────────────────────────── */
            ::-webkit-scrollbar       { width: 5px; }
            ::-webkit-scrollbar-track { background: transparent; }
            ::-webkit-scrollbar-thumb { background: #30363d; border-radius: 3px; }
          </style>
        </head>
        <body>

        <!-- ── Top bar ──────────────────────────────────────────────────── -->
        <!-- In dir="rtl" flex: first child → RIGHT, last child → LEFT     -->
        <div id="topbar">
          <span id="statusdot"></span>
          <span class="logo">Archimedes</span>
          <span class="metric">מעבד: <span class="val" id="m-cpu">—</span></span>
          <span class="metric">זיכרון: <span class="val" id="m-ram">—</span></span>
          <span class="metric">זמן פעולה: <span class="val" id="m-up">—</span></span>
          <span class="version">v0.28.0</span>
        </div>

        <!-- ── Recovery dialogues (Phase 24) ───────────────────────────── -->
        <div id="recovery-area"></div>

        <!-- ── Main layout ───────────────────────────────────────────────── -->
        <!-- First child (chat-area) → RIGHT side in RTL                     -->
        <!-- Second child (tasks-panel) → LEFT side in RTL                   -->
        <div id="main">

          <!-- Chat area (RIGHT) -->
          <div id="chat-area">
            <div id="messages">
              <div class="msg msg-system">
                <div class="intent-chip">SYSTEM</div><br/>שלום! אני Archimedes.<br/>במה אוכל לעזור?
              </div>
            </div>
            <div id="input-bar">
              <!-- textarea first → RIGHT side; button second → LEFT side -->
              <textarea id="msg-input" rows="1" placeholder="הזן הודעה..."></textarea>
              <button id="send-btn" title="שלח">&#x27A4;</button>
            </div>
          </div>

          <!-- Tasks + Goals panel (LEFT) -->
          <div id="tasks-panel">
            <div id="tasks-header">📋 משימות פעילות</div>
            <div id="tasks-list">
              <div id="no-tasks">אין משימות פעילות</div>
            </div>
            <!-- Phase 26: Goals section -->
            <div id="goals-header">🎯 מטרות</div>
            <div id="goals-list">
              <div id="no-goals">אין מטרות פעילות</div>
            </div>
            <!-- Phase 27: Tools section -->
            <div id="tools-header">🔧 כלים</div>
            <div id="tools-list">
              <div id="no-tools" style="color:#8b949e;font-size:.75rem;padding:6px 0">אין כלים נרכשים</div>
            </div>
            <div id="gaps-header" style="display:none">⚠️ חסרים</div>
            <div id="gaps-list"></div>
            <div id="legal-header" style="display:none">⚖️ אישורים</div>
            <div id="legal-list"></div>
            <!-- Phase 28: Migration section -->
            <div id="migration-header" style="margin-top:10px;font-size:.8rem;font-weight:600;color:#58a6ff">🚀 מיגרציה</div>
            <div id="migration-panel" style="margin-top:4px">
              <div style="display:flex;gap:4px;margin-bottom:4px">
                <input id="mig-target" type="text" placeholder="\\\\server\\share  או  http://host:5051"
                  style="flex:1;background:#0d1117;border:1px solid #30363d;color:#e6edf3;padding:4px 6px;border-radius:4px;font-size:.72rem;direction:ltr">
                <button onclick="startMigration(false)"
                  style="background:#1f6feb;border:none;color:#fff;padding:4px 8px;border-radius:4px;cursor:pointer;font-size:.72rem">
                  העבר
                </button>
                <button onclick="startMigration(true)"
                  style="background:#21262d;border:1px solid #30363d;color:#8b949e;padding:4px 6px;border-radius:4px;cursor:pointer;font-size:.72rem"
                  title="Dry-run — בדיקת דיסק בלבד">
                  בדיקה
                </button>
              </div>
              <div id="migration-list" style="font-size:.72rem;color:#8b949e"></div>
            </div>
          </div>

        </div>

        <!-- ── Status bar ────────────────────────────────────────────────── -->
        <div id="statusbar">
          <div class="spin" id="spin"></div>
          <span id="status-txt">מוכן</span>
        </div>

        <script>
          // ── Helpers ─────────────────────────────────────────────────────
          function esc(s) {
            return String(s)
              .replace(/&/g,'&amp;')
              .replace(/</g,'&lt;')
              .replace(/>/g,'&gt;');
          }

          function fmtUptime(sec) {
            if (sec < 60)   return sec + 'ש׳';
            if (sec < 3600) return Math.floor(sec / 60) + 'ד׳';
            const h = Math.floor(sec / 3600);
            const m = Math.floor((sec % 3600) / 60);
            return h + 'ש׳ ' + m + 'ד׳';
          }

          const STATE_LABEL = {
            Running: 'רץ', Pending: 'ממתין',
            Completed: 'הושלם', Failed: 'נכשל',
            Paused: 'מושהה', Cancelled: 'בוטל'
          };
          const STATE_CSS = {
            Running: 's-running', Pending: 's-pending',
            Completed: 's-completed', Failed: 's-failed'
          };

          // ── Polling: metrics ─────────────────────────────────────────────
          async function pollMetrics() {
            try {
              const r = await fetch('/system/metrics');
              if (!r.ok) return;
              const d = await r.json();
              document.getElementById('m-cpu').textContent = d.cpuPercent.toFixed(1) + '%';
              const used  = (d.ramUsedMb  / 1024).toFixed(1);
              const total = (d.ramTotalMb / 1024).toFixed(1);
              document.getElementById('m-ram').textContent = used + '/' + total + 'GB';
              document.getElementById('m-up').textContent  = fmtUptime(d.uptimeSeconds);
              document.getElementById('statusdot').style.cssText =
                'width:8px;height:8px;border-radius:50%;background:#3fb950;box-shadow:0 0 5px #3fb950;flex-shrink:0';
            } catch {
              document.getElementById('statusdot').style.cssText =
                'width:8px;height:8px;border-radius:50%;background:#f85149;box-shadow:0 0 5px #f85149;flex-shrink:0';
            }
          }

          // ── Polling: tasks ───────────────────────────────────────────────
          async function pollTasks() {
            try {
              const r = await fetch('/tasks');
              if (!r.ok) return;
              const all    = await r.json();
              const active = all.filter(t => t.state === 'Running' || t.state === 'Pending');
              const list   = document.getElementById('tasks-list');

              if (active.length === 0) {
                list.innerHTML = '<div id="no-tasks" style="color:#484f58;font-size:.8rem;text-align:center;padding:24px 8px">אין משימות פעילות</div>';
                return;
              }

              list.innerHTML = active.map(t => {
                const css   = STATE_CSS[t.state]  || 's-other';
                const label = STATE_LABEL[t.state] || t.state;
                return `
                  <div class="task-item">
                    <div class="t-id">${esc(t.taskId)}</div>
                    <div class="t-title">${esc(t.title || t.taskId)}</div>
                    <span class="t-state ${css}">${label}</span>
                  </div>`;
              }).join('');
            } catch { /* silent */ }
          }

          // ── Polling: status ──────────────────────────────────────────────
          async function pollStatus() {
            try {
              const r = await fetch('/status/current');
              if (!r.ok) return;
              const d    = await r.json();
              const spin = document.getElementById('spin');
              const txt  = document.getElementById('status-txt');
              if (d.active) {
                spin.classList.add('on');
                txt.textContent = d.description || 'עובד...';
              } else {
                spin.classList.remove('on');
                txt.textContent = 'מוכן';
              }
            } catch { /* silent */ }
          }

          // ── Polling: recovery dialogues (Phase 24) ──────────────────────
          async function pollRecovery() {
            try {
              const r = await fetch('/recovery-dialogues');
              if (!r.ok) return;
              const d    = await r.json();
              const area = document.getElementById('recovery-area');

              if (!d.dialogues || d.dialogues.length === 0) {
                area.innerHTML = '';
                area.classList.remove('has-items');
                return;
              }

              area.classList.add('has-items');
              area.innerHTML = d.dialogues.map(dl => `
                <div class="recovery-card" data-id="${esc(dl.dialogueId)}">
                  <div class="recovery-header">
                    ⚠ שחזור משימה
                  </div>
                  <div class="recovery-task">📋 ${esc(dl.taskTitle || dl.taskId)}</div>
                  <div class="recovery-question">${esc(dl.recoveryQuestion)}</div>
                  <div class="recovery-actions">
                    <button class="rec-btn rec-btn-retry"
                            onclick="recoverRespond('${esc(dl.dialogueId)}','retry')">
                      ↺ נסה שוב
                    </button>
                    <button class="rec-btn rec-btn-dismiss"
                            onclick="recoverRespond('${esc(dl.dialogueId)}','dismiss')">
                      ✕ בטל
                    </button>
                  </div>
                </div>`).join('');
            } catch { /* silent */ }
          }

          async function recoverRespond(dialogueId, action) {
            try {
              const r = await fetch('/recovery-dialogues/' + dialogueId + '/respond', {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify({ action })
              });
              const d = await r.json();
              if (d.ok) {
                const msg = action === 'retry'
                  ? '↺ המשימה הופעלה מחדש'
                  : '✕ המשימה בוטלה';
                appendMsg(msg, 'msg-system');
                pollRecovery();   // refresh immediately
                pollTasks();
              }
            } catch {
              appendMsg('שגיאה בשחזור המשימה', 'msg-system');
            }
          }

          // ── Chat ─────────────────────────────────────────────────────────
          function appendMsg(html, cls) {
            const msgs = document.getElementById('messages');
            const div  = document.createElement('div');
            div.className = 'msg ' + cls;
            div.innerHTML = html;
            msgs.appendChild(div);
            msgs.scrollTop = msgs.scrollHeight;
          }

          async function sendMessage() {
            const input = document.getElementById('msg-input');
            const btn   = document.getElementById('send-btn');
            const text  = input.value.trim();
            if (!text) return;

            input.value = '';
            input.style.height = '40px';
            btn.disabled = true;

            appendMsg(esc(text), 'msg-user');

            const spin = document.getElementById('spin');
            const stxt = document.getElementById('status-txt');
            spin.classList.add('on');
            stxt.textContent = 'מעבד...';

            try {
              const r = await fetch('/chat/message', {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify({ message: text })
              });
              const d = await r.json();

              let html = '';
              if (d.intent) html += `<div class="intent-chip">${esc(d.intent)}</div><br/>`;
              html += esc(d.reply);
              if (d.taskId) html += `<br/><br/><span style="color:#58a6ff;font-size:.8rem">📋 משימה: ${esc(d.taskId)}</span>`;
              if (d.goalId) html += `<br/><br/><span style="color:#3fb950;font-size:.8rem">🎯 מטרה: ${esc(d.goalId)}</span>`;
              appendMsg(html, 'msg-system');
              if (d.goalId) pollGoals();
            } catch {
              appendMsg('שגיאה: לא ניתן להגיע לשרת', 'msg-system');
            }

            btn.disabled = false;
            input.focus();
          }

          document.getElementById('send-btn').addEventListener('click', sendMessage);
          document.getElementById('msg-input').addEventListener('keydown', e => {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); }
          });

          // Auto-resize textarea
          document.getElementById('msg-input').addEventListener('input', function () {
            this.style.height = '40px';
            this.style.height = Math.min(this.scrollHeight, 120) + 'px';
          });

          // ── Polling: goals (Phase 26) ────────────────────────────────────
          const GOAL_STATE_LABEL = {
            ACTIVE: 'פעיל', MONITORING: 'מנטר', IDLE: 'מושהה',
            COMPLETED: 'הושלם', FAILED: 'נכשל'
          };
          const GOAL_STATE_CSS = {
            ACTIVE: 'gs-active', MONITORING: 'gs-monitoring', IDLE: 'gs-idle',
            COMPLETED: 'gs-completed', FAILED: 'gs-failed'
          };

          async function pollGoals() {
            try {
              const r = await fetch('/goals');
              if (!r.ok) return;
              const d    = await r.json();
              const list = document.getElementById('goals-list');

              const interesting = (d.goals || []).filter(g =>
                g.state === 'ACTIVE' || g.state === 'MONITORING' || g.state === 'IDLE');

              if (interesting.length === 0) {
                list.innerHTML = '<div id="no-goals" style="color:#484f58;font-size:.78rem;text-align:center;padding:12px 8px">אין מטרות פעילות</div>';
                return;
              }

              list.innerHTML = interesting.map(g => {
                const css   = GOAL_STATE_CSS[g.state]   || 'gs-idle';
                const label = GOAL_STATE_LABEL[g.state] || g.state;
                const pct   = Math.round((g.progress || 0) * 100);
                return `
                  <div class="goal-item">
                    <div class="g-title" title="${esc(g.goalId)}">${esc(g.title)}</div>
                    <div class="g-meta">
                      <span class="g-state ${css}">${label}</span>
                      <div class="g-progress"><div class="g-progress-bar" style="width:${pct}%"></div></div>
                    </div>
                  </div>`;
              }).join('');
            } catch { /* silent */ }
          }

          // ── Phase 27: Tool acquisition panel ─────────────────────────────
          async function pollTools() {
            try {
              const [toolsResp, gapsResp, legalResp] = await Promise.all([
                fetch('/tools'), fetch('/tools/gaps'), fetch('/tools/legal/pending')
              ]);
              const toolsData = await toolsResp.json();
              const gapsData  = await gapsResp.json();
              const legalData = await legalResp.json();

              // Tools list
              const toolsList = document.getElementById('tools-list');
              if (toolsData.tools && toolsData.tools.length > 0) {
                const RISK_CSS = { SAFE: 'tool-risk-safe', MANAGEABLE: 'tool-risk-mgmt', DANGEROUS: 'tool-risk-dang' };
                const RISK_ICO = { SAFE: '✓', MANAGEABLE: '⚠', DANGEROUS: '✗' };
                toolsList.innerHTML = toolsData.tools.map(t =>
                  `<div class="tool-item">
                    <span class="${RISK_CSS[t.risk] || 'tool-risk-mgmt'}" title="${esc(t.risk)}">${RISK_ICO[t.risk]||'?'}</span>
                    <span class="tool-cap">${esc(t.capability)}</span>
                    <span class="tool-name">${esc(t.name)}</span>
                  </div>`
                ).join('');
              } else {
                toolsList.innerHTML = '<div id="no-tools" style="color:#484f58;font-size:.75rem;padding:6px 0">אין כלים נרכשים</div>';
              }

              // Active gaps
              const gapsHeader = document.getElementById('gaps-header');
              const gapsList   = document.getElementById('gaps-list');
              const activeGaps = (gapsData.gaps || []).filter(g => g.status === 'SEARCHING' || g.status === 'AWAITING_LEGAL');
              if (activeGaps.length > 0) {
                gapsHeader.style.display = '';
                gapsList.innerHTML = activeGaps.map(g =>
                  `<div class="gap-item">
                    🔍 ${esc(g.capability)}
                    <span style="font-size:.68rem;color:#8b949e"> — ${esc(g.status)}</span>
                  </div>`
                ).join('');
              } else {
                gapsHeader.style.display = 'none';
                gapsList.innerHTML = '';
              }

              // Legal approvals
              const legalHeader = document.getElementById('legal-header');
              const legalList   = document.getElementById('legal-list');
              const pending = legalData.approvals || [];
              if (pending.length > 0) {
                legalHeader.style.display = '';
                legalList.innerHTML = pending.map(a =>
                  `<div class="legal-item">
                    <div style="margin-bottom:4px;font-weight:600">${esc(a.capability)}</div>
                    <div style="font-size:.7rem;color:#8b949e;margin-bottom:4px">${esc((a.userMessage||'').substring(0,100))}</div>
                    <div>
                      <button class="legal-btn legal-btn-approve"
                        onclick="decideLegal('${esc(a.approvalId)}','APPROVED')">אשר</button>
                      <button class="legal-btn legal-btn-reject"
                        onclick="decideLegal('${esc(a.approvalId)}','REJECTED')">דחה</button>
                    </div>
                  </div>`
                ).join('');
              } else {
                legalHeader.style.display = 'none';
                legalList.innerHTML = '';
              }
            } catch { /* silent */ }
          }

          async function decideLegal(approvalId, decision) {
            try {
              await fetch(`/tools/legal/${approvalId}/decide`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ decision })
              });
              pollTools();
            } catch { /* silent */ }
          }

          // ── Phase 28: Migration ──────────────────────────────────────────
          async function startMigration(dryRun) {
            const target = document.getElementById('mig-target').value.trim();
            if (!target) { alert('הכנס נתיב יעד'); return; }
            const targetType = target.startsWith('http') ? 'HTTP_URL' : 'LOCAL_PATH';
            try {
              const r = await fetch('/migration/start', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ targetPath: target, targetType, dryRun })
              });
              const d = await r.json();
              pollMigration();
            } catch(e) { alert('שגיאה: ' + e.message); }
          }

          async function pollMigration() {
            try {
              const r = await fetch('/migration/status');
              const d = await r.json();
              const el = document.getElementById('migration-list');
              if (!d.migrations || d.migrations.length === 0) {
                el.innerHTML = '<span style="color:#484f58">אין מיגרציות</span>';
                return;
              }
              const statusColor = { COMPLETED:'#3fb950', FAILED:'#f85149', CHECKING_DISK:'#d29922',
                                    SUSPENDING_TASKS:'#d29922', PACKAGING:'#58a6ff', DEPLOYING:'#58a6ff' };
              el.innerHTML = d.migrations.slice(0, 3).map(m => {
                const col = statusColor[m.status] || '#8b949e';
                const label = m.dryRun ? ' (dry-run)' : '';
                return `<div style="border-left:2px solid ${col};padding:3px 6px;margin-bottom:3px">
                  <span style="color:${col}">${m.status}</span>${label}<br>
                  <span style="direction:ltr;font-size:.68rem">${esc(m.targetPath)}</span><br>
                  ${m.error ? `<span style="color:#f85149;font-size:.68rem">${esc(m.error)}</span><br>` : ''}
                  <span style="color:#484f58;font-size:.65rem">
                    💾 ${m.requiredDiskMB} MB · ${m.taskCount} tasks · ${m.migrationId}
                  </span>
                </div>`;
              }).join('');
            } catch { /* silent */ }
          }

          // ── Start polling ────────────────────────────────────────────────
          pollMetrics();     setInterval(pollMetrics,     5000);
          pollTasks();       setInterval(pollTasks,       3000);
          pollStatus();      setInterval(pollStatus,      2000);
          pollRecovery();    setInterval(pollRecovery,    4000);  // Phase 24
          pollGoals();       setInterval(pollGoals,       5000);  // Phase 26
          pollTools();       setInterval(pollTools,       8000);  // Phase 27
          pollMigration();   setInterval(pollMigration,  10000);  // Phase 28
        </script>

        </body>
        </html>
        """;
}
