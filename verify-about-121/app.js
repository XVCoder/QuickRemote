  // 代码复制按钮
  document.querySelectorAll('.copy-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const text = btn.getAttribute('data-copy');
      navigator.clipboard.writeText(text).then(() => {
        btn.classList.add('copied');
        btn.textContent = '已复制';
        setTimeout(() => { btn.classList.remove('copied'); btn.textContent = '复制'; }, 1600);
      });
    });
  });

  // 滚动入场动画
  const observer = new IntersectionObserver((entries) => {
    entries.forEach(entry => {
      if (entry.isIntersecting) {
        entry.target.classList.add('in');
        observer.unobserve(entry.target);
      }
    });
  }, { threshold: 0.08 });
  document.querySelectorAll('.reveal').forEach(el => observer.observe(el));

  // 下载统计（数据来自 server.js 的 /api/stats）
  (() => {
    const $ = id => document.getElementById(id);
    const host = $('chart-host');
    const yaxis = $('chart-yaxis');
    const readout = $('trend-readout');
    const foot = $('trend-foot');
    const rangeBox = $('trend-range');
    let days = 14;
    let trend = [];
    let top = 1;    // 纵轴上限（整齐偶数，柱高按 total / top 等比）
    let sel = -1;   // 当前读数的那一天

    const num = n => Number(n || 0).toLocaleString('zh-CN');
    const shortDate = d => { const p = d.split('-'); return `${+p[1]}/${+p[2]}`; };

    function setValue(id, value) {
      const el = $(id);
      el.textContent = num(value);
      el.classList.remove('skeleton');
    }

    // 纵轴上限取「整齐的偶数」，保证刻度与柱高都是整数、不出现 7.5 这类刻度
    function niceTop(rawMax) {
      const m = Math.max(1, rawMax);
      if (m <= 5) return m;
      for (let k = 3; k <= 5000; k++) if (k * 2 >= m) return k * 2;
      return m;
    }

    // 刻度值（由高到低）：上限为偶数时补一条中线，便于估读
    function ticksOf(t) {
      const out = [t];
      if (t >= 2 && t % 2 === 0) out.push(t / 2);
      out.push(0);
      return out;
    }

    // 柱高自底部向上生长（旧版把柱子画在顶部，等于倒挂），柱顶标合计值
    function renderChart() {
      const last = trend.length - 1;
      const step = trend.length <= 7 ? 1 : trend.length <= 14 ? 2 : 5;
      const ticks = ticksOf(top);
      const pctOf = v => `${((1 - v / top) * 100).toFixed(2)}%`;

      yaxis.innerHTML = ticks.map(v => `<span style="top:${pctOf(v)}">${num(v)}</span>`).join('');

      const cols = trend.map((t, i) => {
        const cls = 'col' + (i === sel ? ' sel' : '');
        if (!t.total) return `<div class="${cls}" data-i="${i}" data-tip="${t.date}\n暂无下载"></div>`;
        const segs = [];
        if (t.pc) segs.push(`<div class="seg" style="flex:${t.pc}"></div>`);
        if (t.android) segs.push(`<div class="seg android" style="flex:${t.android}"></div>`);
        const tip = `${t.date}\nPC 客户端 ${t.pc}\nAndroid App ${t.android}\n合计 ${t.total}`;
        return `<div class="${cls}" data-i="${i}" data-tip="${tip}">`
          + `<span class="num" style="bottom:calc(${((t.total / top) * 100).toFixed(2)}% + 5px)">${num(t.total)}</span>`
          + `<div style="flex:${top - t.total}"></div>`
          + `<div class="stack" style="flex:${t.total}">${segs.join('')}</div>`
          + `</div>`;
      }).join('');

      const grid = `<div class="chart-grid">${ticks.map(v => `<i style="top:${pctOf(v)}"></i>`).join('')}</div>`;
      const axis = trend.map((t, i) =>
        `<span>${(last - i) % step === 0 ? shortDate(t.date) : ''}</span>`).join('');

      host.innerHTML = `<div class="chart${trend.length > 14 ? ' dense' : ''}">${grid}${cols}</div>`
        + `<div class="chart-axis">${axis}</div>`;
    }

    // 图表下方读数条：始终把选中日的具体数量摆出来（移动端没有 hover 也能看）
    function renderReadout() {
      const t = trend[sel];
      if (!t) { readout.innerHTML = ''; return; }
      readout.innerHTML =
        `<span class="ro-date">${shortDate(t.date)}</span>`
        + `<span class="ro-item"><i style="background:#6799fe"></i>PC 客户端 <b>${num(t.pc)}</b></span>`
        + `<span class="ro-item"><i style="background:#28c840"></i>Android App <b>${num(t.android)}</b></span>`
        + `<span class="ro-item">合计 <b>${num(t.total)}</b></span>`
        + `<span class="ro-hint">点击柱子查看其它日期</span>`;
    }

    function render(data) {
      const pc = data.clients.find(c => c.id === 'pc');
      const android = data.clients.find(c => c.id === 'android');

      setValue('st-total', data.total);
      setValue('st-pc', pc.total);
      setValue('st-android', android.total);
      setValue('st-today', data.today);

      $('st-pc-sub').textContent = pc.version + (pc.baseline ? ` · 含历史基数 ${num(pc.baseline)}` : '');
      $('st-android-sub').textContent = android.version + (android.baseline ? ` · 含历史基数 ${num(android.baseline)}` : '');
      $('st-today-sub').textContent = shortDate(data.range.to);

      if (data.countedTotal > 0) {
        trend = data.trend;
        top = niceTop(Math.max(...trend.map(t => t.total)));
        sel = trend.length - 1;                        // 默认读最近一天，
        while (sel > 0 && !trend[sel].total) sel--;    // 若最近几天没下载则往前找
        renderChart();
        renderReadout();
      } else {
        trend = []; sel = -1;
        yaxis.innerHTML = '';
        readout.innerHTML = '';
        host.innerHTML = '<div class="chart-empty">暂无下载数据<br><span style="font-size:12.5px">统计自服务上线后开始记录，有新下载后即显示趋势</span></div>';
      }

      const range = `${shortDate(data.range.from)} — ${shortDate(data.range.to)}`;
      foot.textContent = data.baselineTotal
        ? `统计自 ${data.since} 起记录，趋势区间 ${range}；另有 ${num(data.baselineTotal)} 次历史下载计入总量（无每日明细）。`
        : `统计自 ${data.since} 起记录，趋势区间 ${range}，按东八区日期归集。`;
    }

    // 点击柱子切换读数（触摸端等效于 hover）
    host.addEventListener('click', e => {
      const col = e.target.closest('.col[data-i]');
      if (!col) return;
      const i = Number(col.dataset.i);
      if (!Number.isFinite(i)) return;
      sel = i;
      renderChart();
      renderReadout();
    });

    async function load() {
      try {
        const res = await fetch(`api/stats?days=${days}`, { cache: 'no-store' });
        if (!res.ok) throw new Error('HTTP ' + res.status);
        render(await res.json());
      } catch (err) {
        yaxis.innerHTML = '';
        readout.innerHTML = '';
        host.innerHTML = '<div class="chart-empty">统计暂时不可用</div>';
        foot.textContent = '无法获取下载统计：' + err.message;
      }
    }

    rangeBox.addEventListener('click', e => {
      const btn = e.target.closest('button[data-days]');
      if (!btn) return;
      const d = Number(btn.dataset.days);
      if (!d || d === days) return;
      days = d;
      rangeBox.querySelectorAll('button').forEach(b => b.classList.toggle('active', b === btn));
      load();
    });

    load();
  })();
