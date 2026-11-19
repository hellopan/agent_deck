// The app half of the shell: binds ui/index.html to real state.
//
// The design board keeps its sample markup. When this file runs inside the
// WebView it fetches state and overwrites the parts that carry data; opened in
// a plain browser it does nothing at all, so the board stays a static mock.
//
// Rules the renderers follow, learned from the WinForms page:
//  · never print a number the process cannot measure (no fake latency, no fake
//    "已连接" — the relay does not tell us who is listening right now);
//  · an empty table gets a sentence, not a blank card;
//  · every action reports its result in the status bar, because a button that
//    does nothing is indistinguishable from a broken one.
(function () {
  "use strict";

  // The one external address the shell is willing to open. Kept in a single
  // place so the about pane, its click handler and the host whitelist agree.
  var GITHUB_URL = "https://github.com/hellopan/agent_deck";

  var bridge = window.chrome && window.chrome.webview;
  if (!bridge || typeof bridge.postMessage !== "function") return;

  // ------------------------------------------------------------- transport

  var seq = 0;
  var pending = new Map();

  function call(command, extra, timeoutMs) {
    return new Promise(function (resolve) {
      var id = ++seq;
      pending.set(id, resolve);
      var message = { deck: command, id: id };
      if (extra) for (var key in extra) message[key] = extra[key];
      bridge.postMessage(message);
      setTimeout(function () {
        if (!pending.has(id)) return;
        pending.delete(id);
        resolve({ error: "超时，宿主没有回应。" });
      }, timeoutMs || 20000);
    });
  }

  bridge.addEventListener("message", function (event) {
    var message = event.data || {};
    if (message.channel === "reply" && pending.has(message.id)) {
      var resolve = pending.get(message.id);
      pending.delete(message.id);
      resolve(message.data);
      return;
    }
    if (message.channel === "state") { S.state = message.data; renderHost(); return; }
    if (message.channel === "notice") { say(message.data.text, message.data.level === "error"); return; }
  });

  // ----------------------------------------------------------------- state

  var S = {
    state: null, tasks: null, logs: null, project: null, logPaused: false, logFilter: "",
    // The live invite lives here, not in the DOM: rebuilding the pane (every
    // reloadState does) must not throw away a code the phone is about to scan.
    pair: null, pairBusy: false, pairFingerprint: "", update: null
  };
  var lastProjects = "";

  function $(selector, root) { return (root || document).querySelector(selector); }
  function $$(selector, root) {
    return Array.prototype.slice.call((root || document).querySelectorAll(selector));
  }
  function esc(value) {
    return String(value === null || value === undefined ? "" : value)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }
  function mono(value) { return '<span class="mono">' + esc(value) + "</span>"; }
  function has(value) { return value !== null && value !== undefined && value !== ""; }

  var statusTimer = 0;
  function say(text, bad) {
    var bar = document.getElementById("status-text");
    if (!bar) return;
    // Keep the dot: a status line without it changes height by two pixels and
    // the whole bar jumps while a result flashes past.
    bar.innerHTML = '<span class="dot ' + (bad ? "" : "gray") + '"></span>' + esc(text);
    bar.style.color = bad ? "var(--red)" : "var(--t3)";
    clearTimeout(statusTimer);
    statusTimer = setTimeout(function () { renderStatus(); }, 6000);
  }

  // ------------------------------------------------------------------ host

  function renderHost() {
    var s = S.state;
    if (!s) return;

    var phones = s.phones || [];
    var pending = S.tasks && S.tasks.pending ? 1 : 0;
    var runningCount = S.tasks && S.tasks.running ? S.tasks.running.length : 0;

    // Top bar: only true statements. "手机可连" needs a paired phone; without
    // one the sentence would promise something the relay cannot deliver.
    var state = document.getElementById("top-state");
    if (state) {
      var tone = s.running ? "" : s.registered ? "amber" : "gray";
      var text = s.running
        ? phones.length > 0 ? "已就绪 · 手机可连" : "运行中 · 等待配对"
        : s.registered ? "已停止 · 手机连不上" : "未注册";
      state.innerHTML = '<span class="dot ' + tone + '"></span>' + esc(text);
      state.style.color = s.running ? "var(--t1)" : s.registered ? "var(--amber)" : "var(--t3)";
    }

    var pendingPill = document.getElementById("top-pending");
    if (pendingPill) {
      pendingPill.style.display = pending > 0 ? "" : "none";
      pendingPill.textContent = pending + " 个待审批";
    }

    var phonePill = document.getElementById("top-phone");
    if (phonePill) {
      phonePill.style.display = phones.length > 0 ? "" : "none";
      phonePill.textContent = phones.length > 0 ? phones[0].name : "";
    }

    var servicePill = document.getElementById("top-service");
    if (servicePill) {
      servicePill.textContent = s.service.running ? "服务模式 · 登录前可用"
        : s.service.installed ? "服务已停 · 手机连不上" : "托盘模式 · 登录后可用";
    }

    var meta = document.getElementById("top-meta");
    if (meta) meta.style.display = "none"; // latency is not measurable from here

    var machine = document.getElementById("tb-machine");
    if (machine) machine.textContent = s.machine;
    var device = document.getElementById("tb-device");
    if (device) device.textContent = has(s.device) ? s.device : "未注册";

    // Nothing lives behind "更多" yet; an icon that opens nothing is worse
    // than no icon.
    var more = document.getElementById("top-more");
    if (more) more.style.display = "none";

    var pause = document.getElementById("top-pause");
    if (pause) {
      if (s.service.running) {
        pause.textContent = "由服务托管";
        pause.disabled = true;
        pause.title = "当前由 Windows 服务托管，请在「设置 → 接入」里停止服务。";
      } else {
        pause.textContent = s.running ? "暂停接收" : "恢复接收";
        pause.disabled = !s.registered;
      }
    }

    renderBanner();
    renderReady();
    renderServiceCard();
    renderStatus();

    var badgeTasks = document.getElementById("badge-tasks");
    if (badgeTasks) {
      badgeTasks.style.display = pending > 0 ? "" : "none";
      badgeTasks.textContent = String(pending);
    }
    var badgeProjects = document.getElementById("badge-projects");
    if (badgeProjects) {
      var enabled = (s.projects || []).filter(function (p) { return p.enabled; }).length;
      badgeProjects.style.display = enabled > 0 ? "" : "none";
      badgeProjects.textContent = String(enabled);
    }

    var foot = document.getElementById("side-foot");
    if (foot) {
      foot.innerHTML = '<span class="dot ' + (runningCount > 0 ? "" : "gray") + '"></span>' +
        (runningCount > 0 ? runningCount + " 个任务在跑" : "没有任务在跑") +
        (pending > 0 ? " · " + pending + " 待审批" : "");
    }

    renderProjects();
    renderSettings();
    renderLogFilters();
  }

  function renderBanner() {
    var banner = document.getElementById("banner");
    var text = document.getElementById("banner-text");
    if (!banner || !text) return;
    var engines = S.state.engines || [];
    var broken = engines.filter(function (e) { return !e.ok; });
    var page = document.querySelector(".page.active");
    var onHost = page && page.dataset.page === "host";
    if (broken.length === 0 || !S.state.running || !onHost) { banner.style.display = "none"; return; }
    banner.style.display = "flex";
    var names = broken.map(function (e) { return e.name; }).join("、");
    text.innerHTML = "<b>" + esc(names) + " 未就绪。</b>手机端会看到该引擎不可用，其余引擎不受影响。";
  }

  function renderReady() {
    var body = document.getElementById("ready-body");
    if (!body) return;
    var ready = S.state.ready;
    var pass = document.getElementById("ready-pass");
    var pend = document.getElementById("ready-pending");
    if (pass) {
      pass.className = "pill " + (ready.pass > 0 ? "ok" : "off");
      pass.innerHTML = '<span class="dot"></span>' + ready.pass + " 项通过";
    }
    if (pend) {
      pend.className = "pill " + (ready.pending > 0 ? "warn" : "off");
      pend.style.display = ready.pending > 0 ? "" : "none";
      pend.textContent = ready.pending + " 项待处理";
    }

    var actions = {
      "中枢通道": '<a class="link muted" data-act="open-logs">日志</a><a class="link" data-act="refresh">重新检查</a>',
      "引擎": '<a class="link" data-go="settings" data-tab="eng">去处理</a><a class="link muted" data-act="redetect">重新检测</a>',
      "项目": '<a class="link" data-go="projects">管理项目</a>',
      "配对": '<a class="link" data-go="settings" data-tab="pair">管理</a>'
    };

    body.innerHTML = ready.rows.map(function (row) {
      return '<div class="row">' +
        '<div class="row-k">' + esc(row.key) + "</div>" +
        '<div class="row-v"><div class="v"' + (row.ok ? "" : ' style="color:var(--amber)"') + ">" +
        esc(row.value) + "</div>" +
        '<div class="sub">' + linkify(row.sub) + "</div></div>" +
        '<div class="row-a">' + (actions[row.key] || "") + "</div>" +
        "</div>";
    }).join("");
    wire(body);
  }

  // Paths and endpoints read as code in the design; keep the mono slabs.
  function linkify(text) {
    var value = esc(text);
    return value.replace(/(wss?:\/\/[^\s·]+)/g, '<span class="mono">$1</span>');
  }

  function renderServiceCard() {
    var kv = document.getElementById("svc-kv");
    if (!kv) return;
    var s = S.state;
    kv.innerHTML =
      cell("服务模式", s.service.mode) +
      cell("Relay 地址", has(s.relay) ? mono(s.relay) : "—") +
      cell("设备 ID", has(s.device) ? mono(s.device) : "未注册") +
      cell("运行时长", s.service.running ? s.uptime : s.running ? s.uptime : "—");
  }

  function cell(label, value) {
    return "<div><div class=\"k\">" + esc(label) + '</div><div class="v">' + value + "</div></div>";
  }

  function renderStatus() {
    var bar = document.getElementById("status-text");
    if (!bar) return;
    var s = S.state;
    if (!s) return;
    var tasks = S.tasks || {};
    var running = (tasks.running || []).length;
    var label = s.running
      ? "服务运行中" + (s.uptime ? " · " + s.uptime : "")
      : s.registered ? "服务已停止" : "未注册";
    bar.innerHTML = '<span class="dot ' + (s.running ? "" : s.registered ? "amber" : "gray") + '"></span>' +
      esc(label) + (has(s.device) ? " · " + esc(s.device) : "");
    bar.style.color = "var(--t3)";
    var mid = document.getElementById("status-mid");
    if (mid) {
      var pending = tasks.pending ? 1 : 0;
      mid.textContent = (pending ? "待处理 " + pending : "无待处理") + " · " +
        (running ? running + " 个任务在跑" : "没有任务在跑");
    }
    var right = document.getElementById("status-right");
    if (right) right.innerHTML = "<span>AGENTDECK " + esc(s.version) + "</span><span>" +
      esc(s.device || "未注册") + "</span>";
  }

  // ----------------------------------------------------------------- tasks

  function renderTasks() {
    var tasks = S.tasks;
    if (!tasks || tasks.error) {
      if (tasks && tasks.error) say("任务列表读不出来：" + tasks.error, true);
      return;
    }
    var running = tasks.running || [];
    var done = tasks.done || [];

    var runningBody = document.getElementById("tasks-running");
    if (runningBody) {
      runningBody.innerHTML = running.length === 0
        ? emptyRow(5, "现在没有任务在跑。手机提交后，电脑空闲时会在这里开始执行。")
        : running.map(function (item) {
          return "<tr><td>" + esc(item.projectId) + "</td><td>" + esc(item.title) + "</td>" +
            '<td class="num">' + esc(item.elapsed) + "</td>" +
            '<td class="muted">' + esc(item.last) + "</td>" +
            '<td style="text-align:right"><a class="link" data-act="trace" data-task="' + esc(item.taskId) + '">查看轨迹</a>' +
            '<a class="link" data-act="cancel" data-project="' + esc(item.projectId) + '" data-task="' + esc(item.taskId) +
            '" style="margin-left:10px;color:var(--red)">叫停</a></td></tr>';
        }).join("");
    }

    var pendingBody = document.getElementById("tasks-pending");
    if (pendingBody) {
      pendingBody.innerHTML = tasks.pending
        ? "<tr><td>" + esc(tasks.pending.projectId) + '</td><td class="mono">' + esc(tasks.pending.text) + "</td>" +
          '<td class="muted">' + esc(tasks.pending.scope) + "</td>" +
          '<td class="num">' + esc(tasks.pending.waited) + "</td><td>" + esc(tasks.pending.waiting) + "</td></tr>"
        : emptyRow(5, "没有等待批准的请求。需要批准时，手机会收到通知。");
    }

    var doneBody = document.getElementById("tasks-done");
    if (doneBody) {
      doneBody.innerHTML = done.length === 0
        ? emptyRow(5, "还没有结束的任务。完成、失败与叫停都会留在这里。")
        : done.map(function (item) {
          return "<tr><td>" + esc(item.projectId) + "</td><td>" + esc(item.title) + "</td>" +
            '<td class="num">' + esc(item.elapsed) + "</td>" +
            '<td class="muted">' + esc(item.end) + "</td><td>" + resultPill(item.status, item.word) + "</td></tr>";
        }).join("");
    }

    var runningPill = document.getElementById("running-pill");
    if (runningPill) {
      runningPill.className = "pill " + (running.length > 0 ? "ok" : "off");
      runningPill.innerHTML = '<span class="dot"></span>' + running.length + " 个";
    }
    var pendingPill = document.getElementById("pending-pill");
    if (pendingPill) {
      pendingPill.className = "pill " + (tasks.pending ? "danger" : "off");
      pendingPill.innerHTML = (tasks.pending ? "1" : "0") + " 条";
    }

    wire(document);
    renderHost();
  }

  function emptyRow(columns, text) {
    return '<tr><td colspan="' + columns + '" class="muted" style="padding:18px 14px">' + esc(text) + "</td></tr>";
  }

  function resultPill(status, word) {
    var tone = status === "completed" ? "ok" : status === "failed" ? "danger" : "off";
    return '<span class="pill ' + tone + '">' + esc(word) + "</span>";
  }

  async function refreshTasks() {
    var data = await call("tasks");
    S.tasks = data;
    renderTasks();
  }

  // -------------------------------------------------------------- projects

  function renderProjects() {
    var list = document.getElementById("proj-list");
    if (!list) return;
    var projects = S.state.projects || [];
    if (!S.project || !projects.some(function (p) { return p.id === S.project; }))
      S.project = projects.length > 0 ? projects[0].id : null;

    // The five-second poll must not rebuild the list under the cursor: a search
    // box that loses focus every tick is unusable. Rebuild only when the data,
    // the selection or the query actually changed.
    var signature = JSON.stringify(projects) + "|" + (S.project || "") + "|" + (S.projectQuery || "");
    if (signature === lastProjects) { renderProjectDetail(); return; }
    lastProjects = signature;

    if (projects.length === 0) {
      list.innerHTML = '<div class="search"><svg width="13" height="13" viewBox="0 0 16 16" fill="none" ' +
        'stroke="currentColor" stroke-width="1.3"><circle cx="7" cy="7" r="4.2"/><path d="M10.2 10.2 13.5 13.5"/></svg>' +
        '<input class="input" placeholder="搜索项目" disabled></div>' +
        '<div class="sub" style="padding:20px 14px">还没有授权项目。手机看不到这台电脑上的任何目录。</div>' +
        '<div class="sub" style="margin-top:auto;padding:10px 12px;border-top:1px solid var(--line2)">' +
        "停用或移除后，手机立即失去访问，正在跑的任务会被取消。</div>";
      renderProjectDetail();
      return;
    }

    var query = (S.projectQuery || "").toLowerCase();
    var visible = projects.filter(function (p) {
      return !query || p.id.toLowerCase().indexOf(query) >= 0 || p.directory.toLowerCase().indexOf(query) >= 0;
    });

    list.innerHTML =
      '<div class="search"><svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" ' +
      'stroke-width="1.3"><circle cx="7" cy="7" r="4.2"/><path d="M10.2 10.2 13.5 13.5"/></svg>' +
      '<input class="input" id="proj-query" placeholder="搜索项目" value="' + esc(S.projectQuery || "") + '"></div>' +
      visible.map(function (p) {
        var tone = !p.enabled ? "off" : p.mode === "workspace-write" ? "ok" : "";
        var word = !p.enabled ? "已停用" : p.mode === "workspace-write" ? "已启用" : "只读";
        return '<div class="proj-item' + (p.id === S.project ? " active" : "") + '" data-proj="' + esc(p.id) + '">' +
          '<div class="pi-top"><span class="pi-name">' + esc(p.id) + '</span><span class="pill ' + tone + '">' +
          word + "</span></div>" + '<div class="pi-path">' + esc(p.directory) + "</div></div>";
      }).join("") +
      (visible.length === 0 ? '<div class="sub" style="padding:16px 14px">没有匹配的项目。</div>' : "") +
      '<div class="sub" style="margin-top:auto;padding:10px 12px;border-top:1px solid var(--line2)">' +
      "停用或移除后，手机立即失去访问，正在跑的任务会被取消。</div>";

    var input = document.getElementById("proj-query");
    if (input) input.addEventListener("input", function () {
      S.projectQuery = input.value;
      renderProjects();
      var again = document.getElementById("proj-query");
      if (again) { again.focus(); again.setSelectionRange(again.value.length, again.value.length); }
    });
    list.querySelectorAll("[data-proj]").forEach(function (node) {
      node.addEventListener("click", function () {
        S.project = node.getAttribute("data-proj");
        renderProjects();
      });
    });
    wire(list);
    renderProjectDetail();
  }

  function renderProjectDetail() {
    var host = document.getElementById("proj-detail");
    if (!host) return;
    var previous = host.querySelector(".tab.active");
    var activeTab = previous ? previous.dataset.tab : "cfg";
    var project = (S.state.projects || []).filter(function (p) { return p.id === S.project; })[0];
    if (!project) {
      host.innerHTML = '<div class="detail-head"><div><h3>没有选中项目</h3>' +
        '<div class="path">添加一个目录后，手机会看到它</div></div></div>' +
        '<div class="pane-pad"><div class="sub">项目页列出的是 Agent 实际读取的授权清单；' +
        "清单为空时，任何远程任务都无法开始。</div></div>";
      return;
    }

    var head = '<div class="detail-head"><div><h3>' + esc(project.id) + '</h3><div class="path">' +
      esc(project.directory) + '</div></div><div class="actions">' +
      '<button class="btn sm" data-act="open-project" data-project="' + esc(project.id) + '">打开目录</button>' +
      '<button class="btn sm" data-act="copy" data-copy="' + esc(project.directory) + '">复制路径</button></div></div>';

    var tabs = '<div class="tabs">' +
      '<button class="tab active" data-tab="cfg">配置</button>' +
      '<button class="tab" data-tab="perm">执行权限</button>' +
      '<button class="tab" data-tab="plog">日志</button></div>';

    var writable = project.mode === "workspace-write";
    var cfg = '<div class="tabpanes active" data-pane="cfg"><div class="pane-pad">' +
      field("显示名称", '<input class="input" readonly value="' + esc(project.id) + '">' +
        '<div class="sub">手机端看到的名称，来自授权清单里的 id，不影响真实路径。</div>') +
      field("真实路径", '<div class="inline"><input class="input" readonly value="' + esc(project.directory) +
        '"><button class="btn sm" data-act="copy" data-copy="' + esc(project.directory) + '">复制</button></div>' +
        '<div class="sub">只读。手机不能浏览目录，也不能提交任意工作目录。</div>') +
      field("启用", '<div style="display:flex;align-items:center;gap:10px;padding-top:4px">' +
        '<span class="switch' + (project.enabled ? " on" : "") + '" data-act="toggle-project" data-project="' +
        esc(project.id) + '" data-enabled="' + (project.enabled ? "false" : "true") + '"></span>' +
        '<span style="font-size:12.5px;color:var(--t2)">' + (project.enabled ? "启用中 · 手机可见" : "已停用 · 手机看不到") +
        "</span></div>") +
      '<div style="display:flex;align-items:center;gap:10px;padding:12px 0 6px"><div>' +
      '<div style="font-size:12.5px;font-weight:500">取消共享</div>' +
      '<div class="sub">从授权清单里移除这个目录，不删除本地文件。项目还有任务在跑时必须先停止。</div></div>' +
      '<button class="btn sm danger" style="margin-left:auto;flex:none" data-act="remove-project" data-project="' +
      esc(project.id) + '">取消共享</button></div></div>' +
      '<div class="save-bar"><span class="state">改动立即写入授权清单，Agent 下次读取时生效</span>' +
      '<div class="actions"><button class="btn" disabled>放弃改动</button>' +
      '<button class="btn primary" disabled>保存</button></div></div></div>';

    var perm = '<div class="tabpanes" data-pane="perm"><div class="pane-pad">' +
      '<div class="sub" style="padding:6px 0 0">策略按项目保存，只作用于当前电脑的这个项目，对后续所有会话生效。' +
      '新加的项目默认只读。</div><div class="group-label">访问范围</div>' +
      opt(writable, "项目目录可写", "子目录继承本目录权限；符号链接与目录联接越界一律拒绝。", "workspace-write") +
      opt(!writable, "项目目录只读", "允许读取、搜索与预览，任何写入直接失败并上报。这是新项目的默认值。", "read-only") +
      "</div><div class=\"save-bar\"><span class=\"state\">" +
      (writable ? "当前：项目目录可写" : "当前：项目目录只读") +
      '</span><div class="actions"><button class="btn" disabled>放弃改动</button>' +
      '<button class="btn primary" disabled>保存</button></div></div></div>';

    var plog = '<div class="tabpanes" data-pane="plog">' + projectLogTable(project) + "</div>";
    host.innerHTML = head + tabs + cfg + perm + plog;
    if (activeTab !== "cfg") {
      host.querySelectorAll(".tab").forEach(function (node) {
        node.classList.toggle("active", node.dataset.tab === activeTab);
      });
      host.querySelectorAll(".tabpanes").forEach(function (pane) {
        pane.classList.toggle("active", pane.dataset.pane === activeTab);
      });
    }
    wire(host);
  }

  function field(label, control) {
    return '<div class="field"><label>' + esc(label) + '</label><div class="ctl">' + control + "</div></div>";
  }

  function opt(active, title, note, mode) {
    return '<div class="opt' + (active ? " on" : "") + '" data-project-mode="' + mode + '">' +
      '<span class="mark"></span><div><div class="t">' + esc(title) + '</div><div class="d">' +
      esc(note) + "</div></div></div>";
  }

  function projectLogTable(project) {
    var events = ((S.logs && S.logs.events) || []).filter(function (event) {
      return projectTaskIds(project.id).indexOf(event.task) >= 0;
    }).slice(0, 40);
    if (events.length === 0)
      return '<div class="sub" style="padding:18px 14px">这个项目还没有任务日志。跑过一次任务后再来看。</div>';
    return '<table class="tbl"><thead><tr><th style="width:80px">时间</th><th style="width:70px">类别</th>' +
      "<th>内容</th></tr></thead><tbody>" + events.map(function (event) {
        return '<tr><td class="num">' + esc(event.at) + '</td><td><span class="lv ' + event.level + '">' +
          esc(event.kind) + '</span></td><td class="mono">' + esc(event.text) + "</td></tr>";
      }).join("") + "</tbody></table>";
  }

  function projectTaskIds(projectId) {
    var tasks = S.tasks || {};
    return (tasks.running || []).concat(tasks.done || [])
      .filter(function (item) { return item.projectId === projectId; })
      .map(function (item) { return item.taskId; });
  }

  // ------------------------------------------------------------------ logs

  function renderLogFilters() {
    var bar = document.getElementById("log-bar");
    if (!bar || bar.dataset.built === "1") return;
    var projects = S.state.projects || [];
    bar.dataset.built = "1";
    bar.innerHTML = '<select class="sel" id="log-project"><option value="">全部项目</option>' +
      projects.map(function (p) { return '<option value="' + esc(p.id) + '">' + esc(p.id) + "</option>"; }).join("") +
      '</select><select class="sel" id="log-kind"><option value="">全部类别</option>' +
      ["连接", "任务", "审批", "命令", "文件", "消息", "错误", "事件"].map(function (kind) {
        return '<option value="' + kind + '">' + kind + "</option>";
      }).join("") + "</select>" +
      '<input class="input" id="log-search" placeholder="搜索内容、路径或方法名" value="' + esc(S.logFilter) + '">' +
      '<div style="margin-left:auto;display:flex;gap:8px;align-items:center">' +
      '<button class="btn sm" data-act="log-pause">' + (S.logPaused ? "继续" : "暂停") + "</button>" +
      '<span class="pill plain"><span class="dot ' + (S.logPaused ? "gray" : "") + '"></span>' +
      (S.logPaused ? "已暂停" : "自动滚底") + "</span></div>";
    var search = document.getElementById("log-search");
    if (search) search.addEventListener("input", function () {
      S.logFilter = search.value;
      renderLogRows();
    });
    $("#log-project", bar).addEventListener("change", renderLogRows);
    $("#log-kind", bar).addEventListener("change", renderLogRows);
    wire(bar);
  }

  function renderLogRows() {
    var body = document.getElementById("log-tbody");
    if (!body) return;
    var all = (S.logs && S.logs.events) || [];
    var projectId = ($("#log-project") || {}).value || "";
    var kind = ($("#log-kind") || {}).value || "";
    var query = (S.logFilter || "").toLowerCase();
    var tasks = projectTaskIds(projectId);

    var visible = all.filter(function (event) {
      if (kind && event.kind !== kind) return false;
      if (projectId && tasks.indexOf(event.task) < 0) return false;
      if (query && (event.text || "").toLowerCase().indexOf(query) < 0 &&
          (event.kind || "").toLowerCase().indexOf(query) < 0) return false;
      return true;
    });

    body.innerHTML = visible.length === 0
      ? emptyRow(3, all.length === 0
        ? "日志目录还是空的。任何任务跑过一次之后，这里才会有内容。"
        : "当前筛选没有匹配的记录。")
      : visible.map(function (event) {
        return '<tr><td class="num">' + esc(event.at) + '</td><td><span class="lv ' + event.level + '">' +
          esc(event.kind) + '</span></td><td class="mono">' + esc(event.text) +
          ' <span class="muted">' + esc(event.task.slice(0, 8)) + "</span></td></tr>";
      }).join("");

    var foot = document.getElementById("log-foot");
    if (foot) {
      foot.innerHTML = '<span><span class="dot ' + (S.logPaused ? "gray" : "") + '"></span> ' +
        (S.logPaused ? "已暂停订阅" : "实时订阅中") + " · 显示 " + visible.length + " 条 · 共 " +
        all.length + " 条</span><span class=\"r\">最后一条 " +
        ((all[0] && all[0].at) || "—") + "</span>";
    }
  }

  async function refreshLogs(limit) {
    S.logs = await call("logs", { limit: limit || 300 });
    renderLogRows();
    renderProjectDetail();
  }

  // -------------------------------------------------------------- settings

  function renderSettings() {
    var page = document.querySelector('.page[data-page="settings"]');
    if (!page) return;
    var s = S.state;

    var net = $('.tabpanes[data-pane="net"]', page);
    if (net && net.dataset.built !== "1") {
      net.dataset.built = "1";
      net.innerHTML = '<div class="pane-pad">' +
        field("设备名称", '<input class="input" readonly value="' + esc(s.machine) + '">' +
          '<div class="sub">手机设备列表里显示的名字，取自这台电脑的计算机名。</div>') +
        field("Relay 地址", '<div class="inline"><input class="input" readonly value="' +
          esc(has(s.relay) ? s.relay : "尚未配置") + '">' +
          '<button class="btn sm" data-act="relay">重新配置…</button></div>' +
          '<div class="sub">保存后立即重连；失败时保留输入并指出下一步。</div>') +
        field("证书指纹", '<input class="input" readonly value="' + esc(S.fingerprint || s.relay) + '">' +
          '<div class="sub">已钉扎。Relay 换证后需要重新配对，否则按失败处理。</div>') +
        field("服务状态", '<div style="display:flex;align-items:center;gap:10px;padding-top:5px">' +
          '<span class="pill ' + (s.service.running ? "ok" : s.service.installed ? "warn" : "off") + '">' +
          '<span class="dot"></span>' + esc(s.service.running ? "运行中 · 登录前可上线"
            : s.service.installed ? "已安装 · 未运行" : "未安装 · 托盘模式") + "</span>" +
          '<button class="btn sm" data-act="service">' + (s.service.installed ? "卸载服务" : "安装服务") + "</button></div>" +
          '<div class="sub">以 Windows 服务运行，托盘退出后继续接收指令。停止后手机立即断开，' +
          "正在执行的任务会被取消。</div>") +
        field("版本与更新", '<div id="update-slot"></div>') +
        '</div><div class="save-bar"><span class="state">接入信息来自本机配置，改动后立即生效</span>' +
        '<div class="actions"><button class="btn" disabled>放弃改动</button>' +
        '<button class="btn primary" disabled>保存并重连</button></div></div>';
      wire(net);
      renderUpdate();
      call("fingerprint").then(function (data) {
        if (!data || !data.value) return;
        S.fingerprint = data.value;
        var field = net.querySelectorAll(".field")[2];
        var input = field && field.querySelector("input");
        if (input) input.value = data.value;
      });
    }

    var eng = $('.tabpanes[data-pane="eng"]', page);
    if (eng && eng.dataset.built !== "1") {
      eng.dataset.built = "1";
      eng.innerHTML = enginesTable() +
        '<div class="sub" style="padding:10px 14px 12px">探测只读本机安装与登录状态，不上传路径。' +
        "未就绪的引擎不出现在手机端。</div>";
      wire(eng);
    }

    var pair = $('.tabpanes[data-pane="pair"]', page);
    if (pair && pair.dataset.built !== "1") {
      pair.dataset.built = "1";
      pair.innerHTML = pairPane();
      wire(pair);
      renderPairQr();
      if (S.pair && S.pair.expiresAt > Date.now()) startPairClock();
    }

    var diag = $('.tabpanes[data-pane="diag"]', page);
    if (diag && diag.dataset.built !== "1") {
      diag.dataset.built = "1";
      diag.innerHTML = diagPane();
      wire(diag);
    }

    var about = $('.tabpanes[data-pane="about"]', page);
    if (about && about.dataset.built !== "1") {
      about.dataset.built = "1";
      about.innerHTML = aboutPane();
      wire(about);
    }
  }

  function renderUpdate() {
    var slot = document.getElementById("update-slot");
    if (!slot) return;
    var s = S.state || {};
    var u = S.update || s.update || {};
    var current = s.version || "—";
    var latest = u.latest || u.version || "";
    var available = !!u.available;
    var label = "当前 v" + current + (available ? " · 发现 v" + latest : " · 已是最新");
    slot.innerHTML =
      '<div style="display:flex;align-items:center;gap:10px;padding-top:5px;flex-wrap:wrap">' +
        '<span class="pill ' + (available ? "warn" : "off") + '"><span class="dot"></span>' +
          esc(label) + '</span>' +
        '<button class="btn sm" data-act="update-check">检查更新</button>' +
        '<button class="btn sm primary" data-act="update-install"' +
          (available ? "" : " disabled") + '>下载并安装</button>' +
      '</div>' +
      '<div class="sub" style="margin-top:6px">' +
        esc(available && u.notes ? u.notes
          : "从 GitHub Releases 获取，校验 SHA-256 后自动重启安装；正在跑的任务会被取消。") + '</div>';
    wire(slot);
  }

  function aboutPane() {
    var current = (S.state && S.state.version) || "—";
    return '<div class="pane-pad">' +
      '<div style="display:flex;align-items:center;gap:12px;margin:2px 0 14px">' +
        '<div class="about-logo"></div>' +
        '<div><div style="font-size:15px;font-weight:600">AgentDeck</div>' +
        '<div class="sub">远程编码工作台</div></div>' +
      '</div>' +
      field("简介", '<div class="sub">桌面端（Windows 托盘）与 Android 端通过 Relay 连接；配对后端到端加密，' +
        "手机上可查看项目、下发任务与审批。</div>") +
      field("版本", '<div style="padding-top:5px"><span class="pill off"><span class="dot"></span>v' +
        esc(current) + '</span></div>' +
        '<div class="sub">更新检查在「接入 → 版本与更新」，从 GitHub Releases 获取，校验 SHA-256 后自动安装。</div>') +
      field("源码与反馈", '<div class="inline">' +
        '<input class="input mono" readonly value="' + esc(GITHUB_URL) + '">' +
        '<button class="btn sm" data-act="open-github">在浏览器中打开</button>' +
        '<button class="btn sm" data-act="copy" data-copy="' + esc(GITHUB_URL) + '">复制地址</button></div>' +
        '<div class="sub">问题反馈、更新日志与新版本下载都在这个仓库的 Issues 与 Releases。</div>') +
      '</div>';
  }

  function enginesTable() {
    var engines = (S.state.engines || []);
    if (engines.length === 0)
      return '<div class="sub" style="padding:18px 14px">正在检测本机引擎…</div>';
    return '<table class="tbl"><thead><tr><th style="width:110px">引擎</th><th style="width:82px">版本</th>' +
      '<th>路径</th><th style="width:96px">状态</th><th style="width:92px"></th></tr></thead><tbody>' +
      engines.map(function (engine) {
        var tone = engine.ok ? "ok" : engine.note === "未安装" ? "off" : "warn";
        var word = engine.ok ? "就绪" : engine.note === "未安装" ? "未安装" : "不可用";
        return "<tr><td>" + esc(engine.name) + '</td><td class="num">' + esc(engine.version || "—") +
          '</td><td class="path">' + esc(engine.path || "—") + '</td><td><span class="pill ' + tone + '">' +
          word + '</span></td><td><a class="link" data-act="redetect">重新检测</a></td></tr>';
      }).join("") + "</tbody></table>";
  }

  // The pairing pane replaced a WinForms dialog that sat on top of this shell
  // with its own blue title bar. Everything the dialog did happens here now:
  // the code is drawn in the left square, the countdown runs under it, the
  // candidate is polled while the code lives, and the fingerprint is confirmed
  // in a sheet instead of a MessageBox.
  function pairPane() {
    return '<div class="pair-wrap"><div class="pair-media">' +
      '<div class="qr" id="pair-qr" data-act="pair"></div>' +
      '<div class="qr-cap" id="pair-cap">配对码两分钟内有效</div></div>' +
      '<div class="pair-info"><div style="display:flex;align-items:center;gap:10px">' +
      '<div style="font-size:12.5px;font-weight:500">用手机扫描，在电脑上确认</div>' +
      '<button class="btn sm" id="pair-btn" data-act="pair" style="margin-left:auto;flex:none">生成配对码</button></div>' +
      '<div class="sub" style="margin-bottom:10px">一次性二维码，过期后重新生成。' +
      '配对后手机私钥进入 Keystore，正常重启无需重新扫码。</div>' +
      '<table class="tbl"><thead><tr><th>已配对设备</th><th style="width:90px">指纹</th>' +
      '<th style="width:96px">配对时间</th><th style="width:70px"></th></tr></thead>' +
      '<tbody id="pair-rows">' + phoneRows() +
      '</tbody></table><div class="sub" style="margin-top:8px">撤销后该手机立即断连，正在跑的任务会被取消。</div></div></div>';
  }

  function phoneRows() {
    var phones = (S.state && S.state.phones) || [];
    if (phones.length === 0)
      return '<tr><td colspan="4" class="muted" style="padding:16px 14px">还没有配对的手机。</td></tr>';
    return phones.map(function (phone) {
      return "<tr><td>" + esc(phone.name) + '</td><td class="mono">' + esc(phone.fingerprint) +
        '</td><td class="muted">' + esc(phone.at) + '</td><td><a class="link" style="color:var(--red)" ' +
        'data-act="revoke" data-fingerprint="' + esc(phone.fingerprint) + '">撤销</a></td></tr>';
    }).join("");
  }

  function clock(seconds) {
    var m = Math.floor(seconds / 60), s = seconds % 60;
    return (m < 10 ? "0" : "") + m + ":" + (s < 10 ? "0" : "") + s;
  }

  function renderPairQr() {
    var box = document.getElementById("pair-qr");
    if (!box) return;
    var cap = document.getElementById("pair-cap");
    var button = document.getElementById("pair-btn");
    var pair = S.pair;
    if (!pair || !pair.png) {
      box.className = "qr";
      box.dataset.png = "";
      box.innerHTML = '<div>二维码</div><div style="font-family:var(--mono);font-size:10px">点此生成</div>';
      if (cap) { cap.className = "qr-cap"; cap.textContent = "配对码两分钟内有效"; }
      if (button) button.textContent = "生成配对码";
      return;
    }
    var left = Math.max(0, Math.round((pair.expiresAt - Date.now()) / 1000));
    // The countdown rewrites this every second, so the bitmap is inserted once
    // and then left alone — re-assigning the same data URL flickered.
    if (box.dataset.png !== pair.png) {
      box.className = "qr has-code";
      box.innerHTML = '<img src="' + pair.png + '" alt="配对二维码">';
      box.dataset.png = pair.png;
    }
    box.classList.toggle("expired", left <= 0);
    if (button) button.textContent = "重新生成";
    if (!cap) return;
    if (left <= 0) { cap.className = "qr-cap warn"; cap.textContent = "已过期 · 重新生成"; }
    else { cap.className = "qr-cap"; cap.textContent = "有效期 " + clock(left); }
  }

  var pairClock = 0;
  function stopPairClock() {
    if (pairClock) { clearInterval(pairClock); pairClock = 0; }
  }
  function startPairClock() {
    stopPairClock();
    pairClock = setInterval(function () {
      var pair = S.pair;
      if (!pair) { stopPairClock(); return; }
      renderPairQr();
      if (pair.expiresAt <= Date.now()) { stopPairClock(); return; }
      if (pair.waiting || Date.now() < pair.nextPoll) return;
      pair.nextPoll = Date.now() + 4000;
      pollPair(pair);
    }, 1000);
  }

  async function pollPair(pair) {
    if (pair.waiting) return;
    pair.waiting = true;
    var data = await call("pair-candidate", { code: pair.code });
    pair.waiting = false;
    if (S.pair !== pair) return;
    if (data && data.ok && data.fingerprint) {
      stopPairClock();
      openPairSheet(data.fingerprint);
      var cap = document.getElementById("pair-cap");
      if (cap) { cap.className = "qr-cap"; cap.textContent = "手机已扫码，等待本机确认"; }
      return;
    }
    if (data && data.expired) { stopPairClock(); renderPairQr(); }
  }

  async function startPair() {
    // The square doubles as the zoom target once a code exists: 176px survives
    // a scaled display, but a 320px copy is what actually scans every time.
    if (S.pair && S.pair.png && S.pair.expiresAt > Date.now()) { openQrSheet(S.pair.png); return; }
    if (S.pairBusy) return;
    S.pairBusy = true;
    var button = document.getElementById("pair-btn");
    if (button) { button.disabled = true; button.textContent = "生成中…"; }
    say("正在生成配对码…");
    var data = await call("pair-start");
    S.pairBusy = false;
    if (button) button.disabled = false;
    if (!data || !data.ok) {
      renderPairQr();
      say((data && data.message) || "配对码生成失败。", true);
      return;
    }
    S.pair = {
      code: data.code, png: data.png,
      expiresAt: Date.now() + (data.expires || 120) * 1000,
      nextPoll: Date.now() + 3000, waiting: false
    };
    renderPairQr();
    startPairClock();
    // The 176px square is a preview, not a scanner target: at this payload the
    // modules land on barely one screen pixel each. Open the full-size copy at
    // once so nobody tries the thumbnail first and concludes it is broken.
    openQrSheet(data.png);
    say("二维码已生成，用手机扫码。");
  }

  function openPairSheet(fingerprint) {
    var sheet = document.getElementById("pair-sheet");
    var field = document.getElementById("pair-sheet-fp");
    if (!sheet || !field) return;
    S.pairFingerprint = fingerprint;
    // The fingerprint is read aloud and compared by eye, so it is re-grouped
    // rather than printed as it arrives: two hex digits per byte, eight bytes
    // per line — the same shape the phone shows, so the two can be read left
    // to right without counting characters.
    var hex = String(fingerprint).toUpperCase().replace(/[^0-9A-F]/g, "");
    if (hex.length >= 32) {
      var bytes = hex.match(/.{1,2}/g) || [];
      var lines = [];
      for (var i = 0; i < bytes.length; i += 8) lines.push(bytes.slice(i, i + 8).join(":"));
      field.innerHTML = lines.map(function (line) { return "<div>" + esc(line) + "</div>"; }).join("");
    } else {
      field.textContent = String(fingerprint);
    }
    sheet.hidden = false;
    var confirm = sheet.querySelector('[data-sheet="confirm"]');
    if (confirm) confirm.focus();
  }

  function openQrSheet(png) {
    var sheet = document.getElementById("qr-sheet");
    var image = document.getElementById("qr-sheet-img");
    if (!sheet || !image) return;
    image.src = png;
    sheet.hidden = false;
  }

  function closeSheets() {
    ["pair-sheet", "qr-sheet"].forEach(function (id) {
      var node = document.getElementById(id);
      if (node) node.hidden = true;
    });
  }

  async function confirmPair() {
    var pair = S.pair;
    var fingerprint = S.pairFingerprint;
    if (!pair || !fingerprint) { closeSheets(); return; }
    var button = document.querySelector('#pair-sheet [data-sheet="confirm"]');
    if (button) { button.disabled = true; button.textContent = "配对中…"; }
    var data = await call("pair-confirm", { code: pair.code, fingerprint: fingerprint });
    if (button) { button.disabled = false; button.textContent = "确认配对"; }
    closeSheets();
    if (!data || !data.ok) { say((data && data.message) || "配对失败。", true); return; }
    S.pair = null;
    S.pairFingerprint = "";
    stopPairClock();
    await reloadState();
    say(data.message || "手机已配对。");
  }

  // Cancelling drops the code entirely instead of resuming the poll: a code
  // that keeps re-announcing the same phone every four seconds is a worse
  // experience than asking for a fresh scan.
  function cancelPair() {
    closeSheets();
    if (!S.pair) return;
    S.pair = null;
    S.pairFingerprint = "";
    stopPairClock();
    renderPairQr();
    say("已取消本次配对，可重新生成。");
  }

  function diagPane() {
    var s = S.state;
    var logs = S.logs || {};
    return '<div class="pane-pad"><div class="kv">' +
      cell("最近 24 小时错误", (logs.errors24h || 0) + " 个") +
      cell("日志条目", (logs.lines || 0) + " 行 · " + (logs.files || 0) + " 个文件") +
      cell("日志目录", '<span class="mono">' + esc(s.paths.logs) + "</span>") +
      cell("证书有效期", s.certDays > 0 ? "剩余 " + s.certDays + " 天" : "—") +
      '</div><div style="display:flex;gap:8px;padding:12px 0 6px">' +
      '<button class="btn sm" data-act="open-logs">打开日志目录</button>' +
      '<button class="btn sm" data-act="copy-env">复制环境信息</button></div>' +
      '<div class="sub">诊断信息只含连接、版本与错误摘要，不含任务正文与凭据。</div></div>';
  }

  // --------------------------------------------------------------- actions

  function wire(root) {
    $$("[data-act]", root).forEach(function (node) {
      if (node.dataset.wired === "1") return;
      node.dataset.wired = "1";
      node.addEventListener("click", function (event) {
        event.preventDefault();
        act(node, event);
      });
    });
    $$("[data-go]", root).forEach(function (node) {
      if (node.dataset.wired === "1") return;
      node.dataset.wired = "1";
      node.addEventListener("click", function (event) {
        event.preventDefault();
        go(node.getAttribute("data-go"), node.getAttribute("data-tab"));
      });
    });
    $$(".opt[data-project-mode]", root).forEach(function (node) {
      if (node.dataset.wired === "1") return;
      node.dataset.wired = "1";
      node.addEventListener("click", function () {
        node.parentElement.querySelectorAll(".opt").forEach(function (other) {
          other.classList.toggle("on", other === node);
        });
        update({ name: "toggle-project", id: S.project, mode: node.getAttribute("data-project-mode") });
      });
    });
  }

  // --------------------------------------------------------------- sheets
  // Both sheets live outside .app, so they survive every pane rebuild and are
  // wired exactly once.
  function wireSheets() {
    document.addEventListener("click", function (event) {
      var role = event.target.closest && event.target.closest("[data-sheet]");
      if (role) {
        var kind = role.getAttribute("data-sheet");
        if (kind === "confirm") confirmPair();
        else if (kind === "cancel") cancelPair();
        else closeSheets();
        return;
      }
      if (event.target.classList && event.target.classList.contains("scrim")) closeSheets();
    });
    document.addEventListener("keydown", function (event) {
      if (event.key !== "Escape") return;
      var sheet = document.getElementById("pair-sheet");
      if (sheet && !sheet.hidden) { cancelPair(); return; }
      closeSheets();
    });
  }

  async function act(node) {
    var action = node.getAttribute("data-act");
    switch (action) {
      case "refresh":
        S.state = await call("state");
        renderHost();
        say("已刷新。");
        return;
      case "redetect":
        say("正在检测引擎…");
        await call("engines");
        S.state = await call("state");
        rebuildSettings();
        renderHost();
        say("引擎状态已更新。");
        return;
      case "open-logs":
        report(await call("action", { name: "open", what: "logs" }));
        return;
      case "open-project":
        report(await call("action", { name: "open", what: "project", projectId: node.getAttribute("data-project") }));
        return;
      case "cancel":
        if (!window.confirm("叫停这个任务？\n\n已开始的命令不会回滚。")) { say("已取消。"); return; }
        report(await call("action", {
          name: "cancel", projectId: node.getAttribute("data-project"), taskId: node.getAttribute("data-task")
        }));
        await refreshTasks();
        return;
      case "trace":
        say("轨迹在手机与日志页查看；桌面端只保留叫停。");
        return;
      case "toggle-project":
        report(await call("action", {
          name: "toggle-project", id: node.getAttribute("data-project"),
          enabled: node.getAttribute("data-enabled")
        }));
        await reloadState();
        return;
      case "remove-project":
        if (!window.confirm("从授权清单移除这个项目？\n\n手机将立即失去访问，本地文件不动。")) { say("已取消。"); return; }
        report(await call("action", { name: "toggle-project", id: node.getAttribute("data-project"), remove: "true" }));
        await reloadState();
        return;
      case "copy":
        navigator.clipboard.writeText(node.getAttribute("data-copy") || "");
        say("已复制。");
        return;
      case "copy-env":
        navigator.clipboard.writeText(environmentText());
        say("环境信息已复制。");
        return;
      case "open-github":
        report(await call("open-url", { url: GITHUB_URL }));
        return;
      case "pair":
        await startPair();
        return;
      case "revoke":
        if (!window.confirm("撤销这台手机的授权？\n\n撤销后需要重新配对才能连接。")) { say("已取消。"); return; }
        report(await call("action", { name: "revoke", fingerprint: node.getAttribute("data-fingerprint") }));
        await reloadState();
        return;
      case "service":
        report(await call("action", { name: "service" }));
        await reloadState();
        return;
      case "relay":
        report(await call("action", { name: "relay" }));
        await reloadState();
        return;
      case "edit-projects":
        report(await call("action", { name: "edit-projects" }));
        S.project = null;
        lastProjects = "";
        await reloadState();
        return;
      case "update-check":
        say("正在检查更新…");
        S.update = await call("update-check");
        renderUpdate();
        if (!S.update.ok) say(S.update.message, true);
        else say(S.update.available ? "发现新版本 v" + S.update.latest + "。" : "已是最新版本。");
        return;
      case "update-install":
        var info = S.update;
        if (!info || !info.available || !info.url) {
          say("正在重新检查更新…");
          info = await call("update-check");
          S.update = info;
          renderUpdate();
        }
        if (!info || !info.ok || !info.available || !info.url) {
          say((info && info.message) || "没有可用的更新。", true);
          return;
        }
        if (!window.confirm("下载并安装 v" + (info.latest || "") +
          "？\n\n安装时 AgentDeck 会自动重启，正在跑的任务会被取消。")) {
          say("已取消。");
          return;
        }
        say("正在下载更新…");
        var downloaded = await call("update-download", {
          url: info.url, sha256: info.sha256, version: info.latest || ""
        }, 600000);
        if (!downloaded.ok) { say(downloaded.message || "下载失败。", true); return; }
        say("校验通过，正在安装并重启…");
        var applied = await call("update-apply", { path: downloaded.path });
        say(applied.message || "正在重启…", !applied.ok);
        return;
      case "pause":
        report(await call("action", { name: "pause" }));
        S.state = await call("state");
        renderHost();
        return;
      case "log-pause":
        S.logPaused = !S.logPaused;
        rebuildLogBar();
        say(S.logPaused ? "已暂停刷新。" : "已恢复刷新。");
        return;
    }
  }

  function update(payload) {
    return call("action", payload).then(reloadState);
  }

  function report(result) {
    if (!result) { say("宿主没有回应。", true); return; }
    say(result.message || (result.ok ? "完成。" : "失败。"), !result.ok);
  }

  function environmentText() {
    var s = S.state;
    return [
      "AgentDeck " + s.version,
      "machine " + s.machine,
      "device " + (s.device || "—"),
      "relay " + (s.relay || "—"),
      "service " + s.service.mode,
      "ready " + s.ready.pass + "/4"
    ].join("\n");
  }

  async function reloadState() {
    S.state = await call("state");
    rebuildSettings();
    renderHost();
    renderProjectDetail();
  }

  function rebuildSettings() {
    ['net', 'eng', 'pair', 'diag'].forEach(function (pane) {
      var node = document.querySelector('.page[data-page="settings"] .tabpanes[data-pane="' + pane + '"]');
      if (node) node.dataset.built = "";
    });
    renderSettings();
  }

  function rebuildLogBar() {
    var bar = document.getElementById("log-bar");
    if (bar) bar.dataset.built = "";
    renderLogFilters();
    renderLogRows();
  }

  // ------------------------------------------------------------ navigation

  function go(page, tab) {
    $$(".page").forEach(function (node) { node.classList.toggle("active", node.dataset.page === page); });
    $$(".nav-item").forEach(function (node) { node.classList.toggle("active", node.dataset.go === page); });
    var banner = document.getElementById("banner");
    if (banner) banner.style.display = page === "host" ? "flex" : "none";
    var content = document.querySelector(".content");
    if (content) content.scrollTop = 0;
    if (tab) {
      var target = document.querySelector('.page.active .tab[data-tab="' + tab + '"]');
      if (target) target.click();
    }
    renderBanner();
    onPage(page);
  }

  function onPage(page) {
    if (page === "tasks") refreshTasks();
    if (page === "logs") refreshLogs();
    if (page === "projects") { reloadState(); refreshLogs(); }
    if (page === "settings") refreshLogs(200);
  }

  // Tabs inside cards: the board's own script already does this, but the panes
  // it toggles are re-created by the renderers, so bind again after each build.
  document.addEventListener("click", function (event) {
    var tab = event.target.closest && event.target.closest(".tab");
    if (!tab) return;
    var card = tab.closest(".card");
    if (!card) return;
    card.querySelectorAll(".tab").forEach(function (node) { node.classList.toggle("active", node === tab); });
    card.querySelectorAll(".tabpanes").forEach(function (pane) {
      pane.classList.toggle("active", pane.dataset.pane === tab.dataset.tab);
    });
  }, true);

  // ------------------------------------------------------------------ boot

  async function boot() {
    wire(document);
    wireSheets();
    S.state = await call("state");
    renderHost();
    await Promise.all([refreshLogs(300), refreshTasks(), call("engines")]);
    S.state = await call("state");
    rebuildSettings();
    renderHost();

    setInterval(async function () {
      S.state = await call("state");
      renderHost();
    }, 5000);

    setInterval(async function () {
      if (S.logPaused) return;
      var page = document.querySelector(".page.active");
      if (!page) return;
      if (page.dataset.page === "tasks") await refreshTasks();
      if (page.dataset.page === "logs" || page.dataset.page === "projects") await refreshLogs(300);
    }, 4000);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();

  // QA: the screenshot hook can open a sheet without a phone to scan with.
  // Harmless in production — it only calls functions this file already runs.
  window.__deck = { startPair: startPair, openPairSheet: openPairSheet };
})();
