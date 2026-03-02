namespace Archimedes.Core;

/// <summary>
/// Phase 22 – Chat UI: Self-contained HTML for the Archimedes chat interface.
/// Served at GET /chat. No external dependencies — vanilla HTML/CSS/JS only.
/// Features: RTL Hebrew, chat area, system metrics bar, tasks panel, status bar.
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
            *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

            body {
              font-family: 'Segoe UI', Arial, sans-serif;
              background: #0d1117;
              color: #e6edf3;
              height: 100vh;
              display: flex;
              flex-direction: column;
              overflow: hidden;
            }

            /* ── Top metrics bar ──────────────────────────────────────── */
            #topbar {
              background: #161b22;
              border-bottom: 1px solid #30363d;
              padding: 7px 16px;
              display: flex;
              align-items: center;
              gap: 20px;
              flex-shrink: 0;
              flex-direction: row-reverse;
            }
            #topbar .logo {
              font-weight: 700;
              font-size: 1rem;
              color: #58a6ff;
              letter-spacing: 1px;
              margin-left: auto;
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
            #main {
              display: flex;
              flex: 1;
              overflow: hidden;
              flex-direction: row;
            }

            /* ── Tasks panel (LEFT side) ──────────────────────────────── */
            #tasks-panel {
              width: 240px;
              border-left: 1px solid #30363d;
              display: flex;
              flex-direction: column;
              background: #0d1117;
              flex-shrink: 0;
              order: 2;
            }
            #tasks-header {
              padding: 10px 12px;
              font-size: 0.75rem;
              font-weight: 700;
              color: #8b949e;
              text-transform: uppercase;
              letter-spacing: 0.5px;
              border-bottom: 1px solid #30363d;
              text-align: right;
            }
            #tasks-list {
              flex: 1;
              overflow-y: auto;
              padding: 8px;
              display: flex;
              flex-direction: column;
              gap: 6px;
            }
            .task-item {
              background: #161b22;
              border: 1px solid #30363d;
              border-radius: 8px;
              padding: 8px 10px;
              font-size: 0.8rem;
              text-align: right;
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

            /* ── Chat area (RIGHT side) ───────────────────────────────── */
            #chat-area {
              flex: 1;
              display: flex;
              flex-direction: column;
              overflow: hidden;
              order: 1;
            }
            #messages {
              flex: 1;
              overflow-y: auto;
              padding: 16px 20px;
              display: flex;
              flex-direction: column;
              gap: 10px;
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
            /* User messages: RIGHT side (RTL = natural end) */
            .msg-user {
              background: #1f6feb;
              color: #fff;
              align-self: flex-start;
              border-bottom-right-radius: 3px;
            }
            /* System messages: LEFT side */
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
            #input-bar {
              padding: 10px 16px;
              border-top: 1px solid #30363d;
              display: flex;
              gap: 8px;
              background: #161b22;
              flex-direction: row-reverse;
            }
            #msg-input {
              flex: 1;
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
              direction: rtl;
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

            /* ── Scrollbar ────────────────────────────────────────────── */
            ::-webkit-scrollbar       { width: 5px; }
            ::-webkit-scrollbar-track { background: transparent; }
            ::-webkit-scrollbar-thumb { background: #30363d; border-radius: 3px; }
          </style>
        </head>
        <body>

        <!-- ── Top bar ──────────────────────────────────────────────────── -->
        <div id="topbar">
          <span id="statusdot"></span>
          <span class="logo">Archimedes</span>
          <span class="metric">מעבד: <span class="val" id="m-cpu">—</span></span>
          <span class="metric">זיכרון: <span class="val" id="m-ram">—</span></span>
          <span class="metric">זמן פעולה: <span class="val" id="m-up">—</span></span>
          <span class="metric" style="margin-right:auto;font-size:.7rem;color:#484f58">v0.22.0</span>
        </div>

        <!-- ── Main layout ───────────────────────────────────────────────── -->
        <div id="main">

          <!-- Chat area (RIGHT) -->
          <div id="chat-area">
            <div id="messages">
              <div class="msg msg-system">
                <div class="intent-chip">SYSTEM</div><br/>שלום! אני Archimedes.<br/>במה אוכל לעזור?
              </div>
            </div>
            <div id="input-bar">
              <button id="send-btn" title="שלח">&#x27A4;</button>
              <textarea id="msg-input" rows="1" placeholder="הזן הודעה..."></textarea>
            </div>
          </div>

          <!-- Tasks panel (LEFT) -->
          <div id="tasks-panel">
            <div id="tasks-header">📋 משימות פעילות</div>
            <div id="tasks-list">
              <div id="no-tasks">אין משימות פעילות</div>
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
            Running:   'רץ', Pending:   'ממתין',
            Completed: 'הושלם', Failed: 'נכשל',
            Paused:    'מושהה', Cancelled: 'בוטל'
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
                'background:#3fb950;box-shadow:0 0 5px #3fb950';
            } catch {
              document.getElementById('statusdot').style.cssText =
                'background:#f85149;box-shadow:0 0 5px #f85149';
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
              const d   = await r.json();
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

            // Show processing in status bar
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
              appendMsg(html, 'msg-system');
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

          // ── Start polling ────────────────────────────────────────────────
          pollMetrics(); setInterval(pollMetrics,  5000);
          pollTasks();   setInterval(pollTasks,    3000);
          pollStatus();  setInterval(pollStatus,   2000);
        </script>

        </body>
        </html>
        """;
}
