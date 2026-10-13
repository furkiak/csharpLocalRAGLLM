'use strict';
(function () {
  const $ = (id) => document.getElementById(id);
  const state = { history: [], documents: [], busy: false, controller: null };

  // ------------------------------------------------------------------ API

  async function api(path, options = {}) {
    const response = await fetch(path, {
      ...options,
      headers: { 'X-LocalRag': '1', ...(options.body && !(options.body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}), ...(options.headers || {}) }
    });
    if (!response.ok && response.status !== 204) {
      let message = response.statusText;
      try { message = (await response.json()).error || message; } catch { /* not JSON */ }
      throw new Error(message);
    }
    return response.status === 204 ? null : response;
  }

  /** POSTs JSON and reads the server-sent events of the answer. */
  async function stream(path, body, handlers, signal) {
    const response = await api(path, { method: 'POST', body: JSON.stringify(body), signal });
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let end;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        let event = 'message', data = '';
        for (const line of block.split('\n')) {
          if (line.startsWith('event:')) event = line.slice(6).trim();
          else if (line.startsWith('data:')) data += line.slice(5).trim();
        }
        if (handlers[event]) handlers[event](data ? JSON.parse(data) : null);
      }
    }
  }

  // ------------------------------------------------------------------ status and library

  async function refreshStatus() {
    const box = $('status'), text = $('status-text');
    try {
      const s = await (await api('/api/status')).json();
      box.className = 'status ' + (s.server.ok ? 'ok' : s.server.error ? 'bad' : 'warn');
      text.textContent = s.server.ok
        ? 'Ready · ' + plural(s.documents, 'document')
        : s.server.error || ('Missing model: ' + (!s.server.chatReady ? s.chatModel : s.embeddingModel));
      const models = $('models');
      models.replaceChildren(
        line('Chat', s.chatModel), line('Embeddings', s.embeddingModel),
        line('Server', s.provider + ' · ' + s.url), line('Index', plural(s.chunks, 'chunk') + ' · ' + size(s.databaseBytes)));
    } catch (e) {
      box.className = 'status bad';
      text.textContent = 'localrag server is not reachable.';
    }
  }

  function line(label, value) {
    const div = document.createElement('div');
    const b = document.createElement('b');
    b.textContent = label + ' ';
    div.append(b, document.createTextNode(value));
    return div;
  }

  async function refreshDocuments() {
    state.documents = await (await api('/api/documents')).json();
    renderDocuments();
  }

  function renderDocuments() {
    const filter = $('filter').value.trim().toLowerCase();
    const list = $('docs');
    const docs = state.documents.filter(d => !filter || d.title.toLowerCase().includes(filter) || d.path.toLowerCase().includes(filter));
    $('doc-count').textContent = state.documents.length;
    $('empty-docs').hidden = state.documents.length > 0;
    list.replaceChildren(...docs.map(d => {
      const li = document.createElement('li');
      li.className = 'doc';
      li.title = d.path;
      const type = document.createElement('span');
      type.className = 'type ' + d.type;
      type.textContent = d.type.slice(0, 4);
      const name = document.createElement('div');
      name.className = 'name';
      const b = document.createElement('b');
      b.textContent = d.title;
      const small = document.createElement('small');
      small.textContent = (d.pages > 1 ? d.pages + ' pages · ' : '') + plural(d.chunks, 'chunk') + ' · ' + size(d.size);
      name.append(b, small);
      const remove = document.createElement('button');
      remove.className = 'remove';
      remove.textContent = '✕';
      remove.setAttribute('aria-label', 'Remove ' + d.title + ' from the library');
      remove.addEventListener('click', async () => {
        try {
          await api('/api/documents?path=' + encodeURIComponent(d.path), { method: 'DELETE' });
          toast('Removed “' + d.title + '” from the library (the file was not touched).');
          await Promise.all([refreshDocuments(), refreshStatus()]);
        } catch (e) { toast(e.message); }
      });
      li.append(type, name, remove);
      return li;
    }));
  }

  // ------------------------------------------------------------------ adding documents

  async function upload(files) {
    if (!files.length) return;
    const form = new FormData();
    for (const f of files) form.append('files', f, f.name);
    showProgress(true, 'Reading ' + files.length + ' file' + (files.length > 1 ? 's' : '') + '…', 30);
    try {
      const r = await (await api('/api/upload', { method: 'POST', body: form })).json();
      const parts = [];
      if (r.added) parts.push(r.added + ' added');
      if (r.updated) parts.push(r.updated + ' updated');
      if (r.unchanged) parts.push(r.unchanged + ' unchanged');
      if (r.failed) parts.push(r.failed + ' failed');
      if (r.rejected.length) parts.push(r.rejected.length + ' unsupported');
      toast(parts.join(' · ') || 'Nothing to add.');
      if (r.errors.length) console.warn(r.errors);
    } catch (e) {
      toast(e.message);
    } finally {
      showProgress(false);
      await Promise.all([refreshDocuments(), refreshStatus()]);
    }
  }

  async function indexFolder(path) {
    showProgress(true, 'Scanning…', 0);
    try {
      await stream('/api/index', { path }, {
        progress: p => showProgress(true, p.done + ' / ' + p.total + ' · ' + p.file, p.total ? p.done / p.total * 100 : 0),
        done: r => toast(r.added + ' added · ' + r.updated + ' updated · ' + r.unchanged + ' unchanged' + (r.removed ? ' · ' + r.removed + ' removed' : '') + (r.failed ? ' · ' + r.failed + ' failed' : '') + ' (' + r.seconds + ' s)'),
        error: e => toast(e.message)
      });
    } catch (e) {
      toast(e.message);
    } finally {
      showProgress(false);
      await Promise.all([refreshDocuments(), refreshStatus()]);
    }
  }

  function showProgress(visible, text, percent) {
    $('progress').hidden = !visible;
    if (!visible) return;
    $('progress-text').textContent = text;
    $('progress-bar').style.width = Math.max(4, Math.min(100, percent || 0)) + '%';
  }

  // ------------------------------------------------------------------ chat

  async function ask(question) {
    if (state.busy) return;
    question = question.trim();
    if (!question) return;
    $('welcome')?.remove();
    addUser(question);
    const { card, answer, sources } = addAnswer();
    setBusy(true);

    let text = '';
    state.controller = new AbortController();
    try {
      await stream('/api/ask', { question, history: state.history }, {
        sources: list => renderSources(sources, list, card),
        token: t => { text += t; answer.innerHTML = renderMarkdown(text); scrollDown(); },
        error: e => { answer.innerHTML = ''; const p = document.createElement('p'); p.className = 'error-text'; p.textContent = e.message; answer.append(p); },
        done: () => {}
      }, state.controller.signal);
      if (text) state.history.push({ role: 'user', content: question }, { role: 'assistant', content: text });
      if (!text && !answer.querySelector('.error-text')) answer.textContent = 'No answer was returned.';
    } catch (e) {
      if (e.name === 'AbortError') {
        if (!text) answer.textContent = 'Stopped.';
      } else {
        answer.innerHTML = '';
        const p = document.createElement('p');
        p.className = 'error-text';
        p.textContent = e.message;
        answer.append(p);
      }
    } finally {
      setBusy(false);
      state.controller = null;
    }
  }

  function addUser(text) {
    const msg = document.createElement('div');
    msg.className = 'msg user';
    const bubble = document.createElement('div');
    bubble.className = 'bubble';
    bubble.textContent = text;
    msg.append(bubble);
    $('messages').append(msg);
    scrollDown();
  }

  function addAnswer() {
    const msg = document.createElement('div');
    msg.className = 'msg ai';
    const card = document.createElement('div');
    card.className = 'card glass';
    const answer = document.createElement('div');
    answer.className = 'answer';
    answer.innerHTML = '<span class="typing"><i></i><i></i><i></i> Searching your documents…</span>';
    const sources = document.createElement('div');
    sources.className = 'sources';
    sources.hidden = true;
    card.append(answer, sources);
    msg.append(card);
    $('messages').append(msg);
    card.addEventListener('click', e => {
      const cite = e.target.closest('.cite');
      if (!cite) return;
      const target = card.querySelector('.source[data-n="' + cite.dataset.n + '"]');
      if (!target) return;
      target.open = true;
      target.classList.remove('flash');
      void target.offsetWidth;
      target.classList.add('flash');
      target.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    });
    scrollDown();
    return { card, answer, sources };
  }

  function renderSources(container, list) {
    container.replaceChildren();
    if (!list.length) return;
    const h = document.createElement('h5');
    h.textContent = 'Sources';
    container.append(h);
    for (const s of list) {
      const details = document.createElement('details');
      details.className = 'source';
      details.dataset.n = s.number;
      const summary = document.createElement('summary');
      const n = document.createElement('span');
      n.className = 'n';
      n.textContent = s.number;
      // Closed: title and the start of the passage, so two passages from one file can be told apart.
      const t = document.createElement('span');
      t.className = 't';
      t.textContent = s.label;
      const small = document.createElement('small');
      small.textContent = s.text.replace(/[#*_`>|]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 140);
      t.append(small);
      summary.append(n, t);
      const path = document.createElement('span');
      path.className = 'path';
      path.textContent = s.path;
      const p = document.createElement('p');
      p.textContent = s.text;
      details.append(summary, path, p);
      container.append(details);
    }
    container.hidden = false;
  }

  /** A deliberately small Markdown subset. The text is HTML-escaped first, so model output can never inject markup. */
  function renderMarkdown(source) {
    const escape = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    const inline = s => escape(s)
      .replace(/`([^`]+)`/g, '<code>$1</code>')
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
      .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>')
      .replace(/\[(\d{1,2})\]/g, '<button class="cite" data-n="$1" type="button" aria-label="Source $1">$1</button>');

    const out = [];
    const lines = source.replace(/\r/g, '').split('\n');
    let list = null, para = [], code = null;
    const flushPara = () => { if (para.length) { out.push('<p>' + para.map(inline).join('<br>') + '</p>'); para = []; } };
    const flushList = () => { if (list) { out.push('<' + list.tag + '>' + list.items.map(i => '<li>' + inline(i) + '</li>').join('') + '</' + list.tag + '>'); list = null; } };

    for (const raw of lines) {
      if (code !== null) {
        if (raw.trim().startsWith('```')) { out.push('<pre><code>' + escape(code.join('\n')) + '</code></pre>'); code = null; }
        else code.push(raw);
        continue;
      }
      const line = raw.trimEnd();
      if (line.trim().startsWith('```')) { flushPara(); flushList(); code = []; continue; }
      const bullet = /^\s*[-*•]\s+(.*)$/.exec(line);
      const numbered = /^\s*\d+[.)]\s+(.*)$/.exec(line);
      const heading = /^#{1,4}\s+(.*)$/.exec(line);
      if (heading) { flushPara(); flushList(); out.push('<h4>' + inline(heading[1]) + '</h4>'); }
      else if (bullet || numbered) {
        flushPara();
        const tag = bullet ? 'ul' : 'ol';
        if (!list || list.tag !== tag) { flushList(); list = { tag, items: [] }; }
        list.items.push((bullet || numbered)[1]);
      } else if (!line.trim()) { flushPara(); flushList(); }
      else { flushList(); para.push(line); }
    }
    if (code !== null) out.push('<pre><code>' + escape(code.join('\n')) + '</code></pre>');
    flushPara(); flushList();
    return out.join('');
  }

  function setBusy(busy) {
    state.busy = busy;
    const send = $('send');
    send.classList.toggle('stop', busy);
    send.textContent = busy ? '■' : '↑';
    send.setAttribute('aria-label', busy ? 'Stop' : 'Send');
  }

  function scrollDown() { const m = $('messages'); m.scrollTop = m.scrollHeight; }

  function plural(count, word) { return count + ' ' + word + (count === 1 ? '' : 's'); }

  function size(bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1).replace(/\.0$/, '') + ' KB';
    return (bytes / 1024 / 1024).toFixed(1).replace(/\.0$/, '') + ' MB';
  }

  function toast(message) {
    const t = $('toast');
    t.textContent = message;
    t.hidden = false;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => { t.hidden = true; }, 4200);
  }

  // ------------------------------------------------------------------ wiring

  const question = $('question');
  $('composer').addEventListener('submit', e => {
    e.preventDefault();
    if (state.busy) { state.controller?.abort(); return; }
    const text = question.value;
    question.value = '';
    question.style.height = '';
    ask(text);
  });
  question.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); $('composer').requestSubmit(); }
  });
  question.addEventListener('input', () => { question.style.height = 'auto'; question.style.height = Math.min(question.scrollHeight, 180) + 'px'; });

  $('suggestions').addEventListener('click', e => { const chip = e.target.closest('.chip'); if (chip) ask(chip.textContent); });
  $('new-chat').addEventListener('click', () => {
    state.controller?.abort();
    state.history = [];
    $('messages').replaceChildren();
    const p = document.createElement('p');
    p.className = 'muted';
    p.style.textAlign = 'center';
    p.style.margin = 'auto';
    p.textContent = 'New conversation. Ask a question about your documents.';
    $('messages').append(p);
    question.focus();
  });

  $('file-input').addEventListener('change', e => { upload([...e.target.files]); e.target.value = ''; });
  const drop = $('drop');
  drop.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); $('file-input').click(); } });
  for (const name of ['dragenter', 'dragover']) drop.addEventListener(name, e => { e.preventDefault(); drop.classList.add('over'); });
  for (const name of ['dragleave', 'drop']) drop.addEventListener(name, e => { e.preventDefault(); drop.classList.remove('over'); });
  drop.addEventListener('drop', e => upload([...e.dataTransfer.files]));
  window.addEventListener('dragover', e => e.preventDefault());
  window.addEventListener('drop', e => e.preventDefault());

  $('folder-form').addEventListener('submit', e => {
    e.preventDefault();
    const path = $('folder-path').value.trim();
    if (path) indexFolder(path);
  });
  $('filter').addEventListener('input', renderDocuments);
  $('open-sidebar').addEventListener('click', () => $('sidebar').classList.add('open'));
  $('close-sidebar').addEventListener('click', () => $('sidebar').classList.remove('open'));

  refreshStatus();
  refreshDocuments().catch(e => toast(e.message));
  question.focus();
})();
