// ScreenTranslator WebView2 前端：流式对话渲染 + 历史列表 + 倾听/响应特效
(function () {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const chat = $("chat");
  const wordEl = $("word");
  const input = $("input");
  const sendBtn = $("btn-send");
  const chatView = $("chat-view");
  const historyView = $("history-view");
  const historyList = $("history-list");
  const historySearch = $("history-search");
  const wave = $("wave");

  let streaming = false;
  let streamBody = null;      // 当前流式消息 body 元素
  let currentText = "";       // 当前选中词
  let lastChunk = "";         // 最近一次累积快照
  let locked = false;         // 截图识别中：仅设置按钮可点

  // 锁定/解锁面板交互：识别+首轮输出完成前，除设置按钮外全部禁用
  function setLocked(v) {
    locked = v;
    input.disabled = v;
    sendBtn.disabled = v || streaming;
    $("btn-speak").disabled = v;
    $("btn-close").disabled = v;
    $("btn-history").disabled = v;
    $("btn-settings").disabled = false;   // 设置始终可用
    if (v) historySearch.blur();
  }

  // ---------- Markdown 简易渲染（安全转义） ----------
  function esc(s) {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }
  function md(text) {
    let h = esc(text);
    h = h.replace(/```([\s\S]*?)```/g, (_, c) => "<pre><code>" + c.trim() + "</code></pre>");
    h = h.replace(/`([^`]+)`/g, "<code>$1</code>");
    h = h.replace(/^### (.*)$/gm, "<h3>$1</h3>");
    h = h.replace(/^## (.*)$/gm, "<h2>$1</h2>");
    h = h.replace(/^# (.*)$/gm, "<h1>$1</h1>");
    h = h.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    h = h.replace(/\*([^*]+)\*/g, "<em>$1</em>");
    h = h.replace(/^(?:[-*] )(.*)$/gm, "<li>$1</li>");
    h = h.replace(/(<li>[\s\S]*?<\/li>)(?!\s*<li>)/g, "<ul>$1</ul>");
    h = h.replace(/^---+$/gm, "<hr/>");
    h = h.split(/\n{2,}/).map(p =>
      /^\s*<(h\d|ul|li|pre|hr)/.test(p) ? p : "<p>" + p.replace(/\n/g, "<br/>") + "</p>"
    ).join("");
    return h;
  }

  function addMsg(role, text) {
    const wrap = document.createElement("div");
    wrap.className = "msg " + role;
    const roleEl = document.createElement("div");
    roleEl.className = "role";
    roleEl.textContent = role === "user" ? "我" : "AI";
    const body = document.createElement("div");
    body.className = "body";
    wrap.appendChild(roleEl);
    wrap.appendChild(body);
    chat.appendChild(wrap);
    chat.scrollTop = chat.scrollHeight;
    if (text !== undefined) body.innerHTML = md(text);
    return body;
  }

  function addError(text) {
    const el = document.createElement("div");
    el.className = "err";
    el.textContent = "出错：" + text;
    chat.appendChild(el);
    chat.scrollTop = chat.scrollHeight;
  }

  window.addEventListener("error", function (e) {
    try {
      window.chrome.webview.postMessage({
        type: "jsError",
        message: e.message + " @" + (e.filename || "") + ":" + e.lineno
      });
    } catch (_) {}
  });

  // ---------- 与 C# 通信 ----------
  function post(msg) {
    // 直接传对象：postMessage 字符串会被 WebView2 再包一层引号，导致 C# 解析失败
    window.chrome.webview.postMessage(msg);
  }

  window.chrome.webview.addEventListener("message", (ev) => {
    const m = ev.data;
    switch (m.type) {
      case "config":
        break;
      case "state":
        // 状态特效：thinking=响应中 / listening=倾听中 / idle=隐藏
        if (m.state === "idle") {
          wave.classList.add("hidden");
        } else {
          wave.classList.remove("hidden");
          wave.style.setProperty("--wave-color",
            m.state === "listening" ? "#5fd98a" : "var(--bright)");
        }
        break;
      case "newWord":
        currentText = m.word || "";
        if (m.word) wordEl.textContent = m.word;
        chat.innerHTML = "";
        showView("chat");   // 面板可能上次停在历史列表，打开新会话时强制回到对话界面
        input.focus();
        break;
      case "userMsg":
        addMsg("user", m.text);
        break;
      case "streamStart":
        streaming = true;
        sendBtn.disabled = true;
        streamBody = addMsg("assistant");
        streamBody.innerHTML = '<span class="cursor"></span>';
        break;
      case "chunk":
        lastChunk = m.delta;
        if (streamBody) {
          streamBody.innerHTML = md(m.delta) + '<span class="cursor"></span>';
          chat.scrollTop = chat.scrollHeight;
        }
        break;
      case "streamEnd":
        streaming = false;
        sendBtn.disabled = false;
        if (streamBody) {
          streamBody.innerHTML = md(lastChunk || streamBody.textContent.replace("", ""));
          streamBody = null;
          lastChunk = "";
        }
        chat.scrollTop = chat.scrollHeight;
        break;
      case "error":
        addError(m.message);
        break;
      case "screenshotPending":
        // 截图识别中：强制切到对话界面 + 占位标题 + 锁定（仅设置按钮可用）
        showView("chat");
        wordEl.textContent = "正在识别屏幕文字…";
        setLocked(true);
        break;
      case "unlock":
        setLocked(false);
        break;
      case "title":
        if (m.text) wordEl.textContent = m.text;   // 标题生成完成，替换 OCR 截断词
        break;
      case "historyList":
        renderHistory(m.items);
        break;
      case "loadSession":
        currentText = m.word;
        wordEl.textContent = m.word;
        chat.innerHTML = "";
        (m.messages || []).forEach(msg => {
          if (msg.role === "system") return;
          addMsg(msg.role === "user" ? "user" : "assistant", msg.content);
        });
        showView("chat");
        break;
    }
  });

  function renderHistory(items) {
    historyList.innerHTML = "";
    if (!items || !items.length) {
      historyList.innerHTML =
        '<div class="h-empty">' +
        (historySearch.value.trim() ? "没有匹配的记录" : "暂无查词记录") +
        "</div>";
      return;
    }
    items.forEach(it => {
      const el = document.createElement("div");
      el.className = "h-item";
      const w = document.createElement("div");
      w.className = "h-word";
      w.textContent = it.word;
      const meta = document.createElement("div");
      meta.className = "h-meta";
      meta.textContent = it.time + " · " + it.count + " 次提问";
      const del = document.createElement("button");
      del.className = "h-del";
      del.textContent = "×";
      del.title = "删除该记录";
      del.onclick = (e) => {
        e.stopPropagation();
        post({ type: "deleteHistory", word: it.word, keyword: historySearch.value });
      };
      el.appendChild(w);
      el.appendChild(meta);
      el.appendChild(del);
      el.onclick = () => post({ type: "openHistoryItem", word: it.word });
      historyList.appendChild(el);
    });
  }

  function showView(v) {
    if (v === "history") {
      historyView.classList.remove("hidden");
      chatView.classList.add("hidden");
      historySearch.value = "";
      post({ type: "requestHistory" });
      historySearch.focus();
    } else {
      chatView.classList.remove("hidden");
      historyView.classList.add("hidden");
    }
  }

  // 搜索：输入即时过滤（200ms 防抖），由 C# 端执行
  let searchTimer = null;
  historySearch.addEventListener("input", () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      post({ type: "requestHistory", keyword: historySearch.value });
    }, 200);
  });

  // ---------- 交互 ----------
  $("btn-speak").onclick = () => post({ type: "speak", text: currentText });
  $("btn-close").onclick = () => post({ type: "close" });
  $("btn-settings").onclick = () => post({ type: "openSettings" });
  $("btn-history").onclick = () =>
    historyView.classList.contains("hidden") ? showView("history") : showView("chat");

  $("input-row").onsubmit = (e) => {
    e.preventDefault();
    const text = input.value.trim();
    if (!text || streaming || locked) return;
    input.value = "";
    post({ type: "ask", text });
  };

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      // 搜索框有内容时先清空搜索，再按才关闭面板
      if (document.activeElement === historySearch && historySearch.value) {
        historySearch.value = "";
        post({ type: "requestHistory" });
        return;
      }
      post({ type: "close" });
    }
  });

  // 就绪
  post({ type: "ready" });
})();
