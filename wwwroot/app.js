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

  // ---------- Markdown 渲染（代码块 / 表格 / 基础排版，安全转义） ----------
  function esc(s) {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }

  function inlineMd(s) {
    s = s.replace(/`([^`]+)`/g, "<code>$1</code>");
    s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    s = s.replace(/\*([^*]+)\*/g, "<em>$1</em>");
    return s;
  }

  function codeBlockHtml(block) {
    const lang = esc(block.lang || "text");
    const code = esc(block.code);
    return (
      '<div class="code-block">' +
        '<div class="code-head">' +
          '<span class="code-lang">' + lang + '</span>' +
          '<div class="code-actions">' +
            '<button type="button" class="code-btn" data-code-copy title="复制代码">' +
              '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="9" width="11" height="11" rx="2"></rect><path d="M5 15V5a2 2 0 0 1 2-2h10"></path></svg>' +
              '<span>复制</span>' +
            '</button>' +
            '<button type="button" class="code-btn" data-code-download title="下载代码">' +
              '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v12"></path><path d="m7 10 5 5 5-5"></path><path d="M5 21h14"></path></svg>' +
              '<span>下载</span>' +
            '</button>' +
          '</div>' +
        '</div>' +
        '<pre class="code-body"><code>' + code + '</code></pre>' +
      '</div>'
    );
  }

  function splitTableRow(line) {
    let s = line.trim();
    if (s.startsWith("|")) s = s.slice(1);
    if (s.endsWith("|")) s = s.slice(0, -1);
    return s.split("|").map(c => c.trim());
  }

  function isTableSeparator(line) {
    if (!line || line.indexOf("|") < 0) return false;
    const cells = splitTableRow(line);
    return cells.length > 0 && cells.every(c => /^:?-{3,}:?$/.test(c));
  }

  function tableHtml(header, aligns, rows) {
    const alignAttr = i => aligns[i] ? ' style="text-align:' + aligns[i] + '"' : "";
    const head = header.map((c, i) => "<th" + alignAttr(i) + ">" + inlineMd(c) + "</th>").join("");
    const body = rows.map(r =>
      "<tr>" + header.map((_, i) => "<td" + alignAttr(i) + ">" + inlineMd(r[i] || "") + "</td>").join("") + "</tr>"
    ).join("");
    return '<div class="table-wrap"><table><thead><tr>' + head + '</tr></thead><tbody>' + body + '</tbody></table></div>';
  }

  function md(text) {
    const blocks = [];
    let src = String(text || "").replace(/```([^\n`]*)\r?\n([\s\S]*?)(?:```|$)/g, (_, info, code) => {
      const lang = (info || "").trim().split(/\s+/)[0] || "text";
      const idx = blocks.length;
      blocks.push({ lang: lang, code: code.replace(/\r\n/g, "\n").replace(/\n$/, "") });
      return "\n\n@@CODEBLOCK" + idx + "@@\n\n";
    });

    const lines = esc(src).split(/\r?\n/);
    const out = [];
    let para = [];
    const flushPara = () => {
      if (!para.length) return;
      out.push("<p>" + inlineMd(para.join("<br/>")) + "</p>");
      para = [];
    };

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const trimmed = line.trim();
      if (!trimmed) { flushPara(); continue; }

      const codeMatch = trimmed.match(/^@@CODEBLOCK(\d+)@@$/);
      if (codeMatch) {
        flushPara();
        out.push(trimmed);
        continue;
      }

      if (trimmed.indexOf("|") >= 0 && i + 1 < lines.length && isTableSeparator(lines[i + 1])) {
        flushPara();
        const header = splitTableRow(trimmed);
        const sep = splitTableRow(lines[i + 1]);
        const aligns = sep.map(c => c.startsWith(":") && c.endsWith(":") ? "center" : c.endsWith(":") ? "right" : "left");
        const rows = [];
        i += 2;
        while (i < lines.length && lines[i].trim() && lines[i].indexOf("|") >= 0) {
          rows.push(splitTableRow(lines[i]));
          i++;
        }
        i--;
        out.push(tableHtml(header, aligns, rows));
        continue;
      }

      const heading = trimmed.match(/^(#{1,6})\s+(.*)$/);
      if (heading) {
        flushPara();
        const level = heading[1].length;
        out.push("<h" + level + ">" + inlineMd(heading[2]) + "</h" + level + ">");
        continue;
      }

      if (/^&gt;\s?/.test(trimmed)) {
        flushPara();
        const quote = [];
        while (i < lines.length && /^&gt;\s?/.test(lines[i].trim())) {
          quote.push(lines[i].trim().replace(/^&gt;\s?/, ""));
          i++;
        }
        i--;
        out.push("<blockquote>" + inlineMd(quote.join("<br/>")) + "</blockquote>");
        continue;
      }

      if (/^---+$/.test(trimmed)) { flushPara(); out.push("<hr/>"); continue; }

      if (/^[-*]\s+/.test(trimmed)) {
        flushPara();
        const items = [];
        while (i < lines.length && /^[-*]\s+/.test(lines[i].trim())) {
          items.push("<li>" + inlineMd(lines[i].trim().replace(/^[-*]\s+/, "")) + "</li>");
          i++;
        }
        i--;
        out.push("<ul>" + items.join("") + "</ul>");
        continue;
      }

      para.push(line);
    }
    flushPara();

    return out.join("").replace(/@@CODEBLOCK(\d+)@@/g,
      (_, idx) => codeBlockHtml(blocks[Number(idx)] || { lang: "text", code: "" }));
  }

  async function writeClipboard(text) {
    try { await navigator.clipboard.writeText(text); return true; }
    catch (_) {
      try {
        const ta = document.createElement("textarea");
        ta.value = text;
        ta.style.position = "fixed";
        ta.style.opacity = "0";
        document.body.appendChild(ta);
        ta.select();
        const ok = document.execCommand("copy");
        ta.remove();
        return ok;
      } catch (_) { return false; }
    }
  }

  chat.addEventListener("click", async (e) => {
    if (!(e.target instanceof Element)) return;
    const copyBtn = e.target.closest("[data-code-copy]");
    if (copyBtn) {
      const code = copyBtn.closest(".code-block")?.querySelector("pre code")?.textContent || "";
      const ok = await writeClipboard(code);
      const label = copyBtn.querySelector("span");
      if (label) {
        const old = label.textContent;
        label.textContent = ok ? "已复制" : "复制失败";
        setTimeout(() => { label.textContent = old; }, 1200);
      }
      return;
    }
    const dlBtn = e.target.closest("[data-code-download]");
    if (dlBtn) {
      const block = dlBtn.closest(".code-block");
      const code = block?.querySelector("pre code")?.textContent || "";
      const lang = block?.querySelector(".code-lang")?.textContent.trim().toLowerCase() || "text";
      const ext = ({ javascript: "js", typescript: "ts", csharp: "cs", "c#": "cs", python: "py",
        shell: "sh", bash: "sh", json: "json", html: "html", css: "css", sql: "sql", java: "java",
        cpp: "cpp", c: "c", go: "go", rust: "rs", text: "txt" })[lang] || "txt";
      const url = URL.createObjectURL(new Blob([code], { type: "text/plain;charset=utf-8" }));
      const a = document.createElement("a");
      a.href = url;
      a.download = "code." + ext;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
  });

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
