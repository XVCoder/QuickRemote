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
    const foot = $('trend-foot');
    const rangeBox = $('trend-range');
    let days = 14;

    const num = n => Number(n || 0).toLocaleString('zh-CN');
    const shortDate = d => { const p = d.split('-'); return `${+p[1]}/${+p[2]}`; };

    function setValue(id, value) {
      const el = $(id);
      el.textContent = num(value);
      el.classList.remove('skeleton');
    }

    // 用 flex 比例控制柱高与分段高度，避免依赖百分比高度
    function renderChart(trend) {
      const max = Math.max(1, ...trend.map(t => t.total));
      const last = trend.length - 1;
      const step = trend.length <= 7 ? 1 : trend.length <= 14 ? 2 : 5;

      const cols = trend.map(t => {
        if (!t.total) return `<div class="col" data-tip="${t.date}\n暂无下载"></div>`;
        const segs = [];
        if (t.pc) segs.push(`<div class="seg" style="flex:${t.pc}"></div>`);
        if (t.android) segs.push(`<div class="seg android" style="flex:${t.android}"></div>`);
        const tip = `${t.date}\nPC 客户端 ${t.pc}\nAndroid App ${t.android}\n合计 ${t.total}`;
        return `<div class="col" data-tip="${tip}">`
          + `<div class="stack" style="flex:${t.total}">${segs.join('')}</div>`
          + `<div style="flex:${max - t.total}"></div>`
          + `</div>`;
      }).join('');

      const axis = trend.map((t, i) =>
        `<span>${(last - i) % step === 0 ? shortDate(t.date) : ''}</span>`).join('');

      host.innerHTML = `<div class="chart">${cols}</div><div class="chart-axis">${axis}</div>`;
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
        renderChart(data.trend);
      } else {
        host.innerHTML = '<div class="chart-empty">暂无下载数据<br><span style="font-size:12.5px">统计自服务上线后开始记录，有新下载后即显示趋势</span></div>';
      }

      const range = `${shortDate(data.range.from)} — ${shortDate(data.range.to)}`;
      foot.textContent = data.baselineTotal
        ? `统计自 ${data.since} 起记录，趋势区间 ${range}；另有 ${num(data.baselineTotal)} 次历史下载计入总量（无每日明细）。`
        : `统计自 ${data.since} 起记录，趋势区间 ${range}，按东八区日期归集。`;
    }

    async function load() {
      try {
        const res = await fetch(`api/stats?days=${days}`, { cache: 'no-store' });
        if (!res.ok) throw new Error('HTTP ' + res.status);
        render(await res.json());
      } catch (err) {
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
