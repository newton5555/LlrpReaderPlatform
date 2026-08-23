// LlrpReaderPlatform UI Showcase Scripts
document.addEventListener('DOMContentLoaded', () => {
  // Mobile Menu Toggle
  const menuToggle = document.querySelector('.menu-toggle');
  const topbarNav = document.querySelector('nav.topbar-nav');
  if (menuToggle && topbarNav) {
    menuToggle.addEventListener('click', () => {
      const isVisible = topbarNav.style.display === 'flex';
      topbarNav.style.display = isVisible ? 'none' : 'flex';
      if (!isVisible) {
        topbarNav.style.position = 'absolute';
        topbarNav.style.top = '70px';
        topbarNav.style.left = '0';
        topbarNav.style.right = '0';
        topbarNav.style.background = '#090d16';
        topbarNav.style.flexDirection = 'column';
        topbarNav.style.padding = '20px';
        topbarNav.style.borderBottom = '1px solid rgba(148, 163, 184, 0.15)';
      }
    });
  }

  // Copy Code
  document.querySelectorAll('.copy-btn').forEach(btn => {
    btn.addEventListener('click', async () => {
      const pre = btn.parentElement.querySelector('pre');
      if (!pre) return;
      try {
        await navigator.clipboard.writeText(pre.innerText.trim());
        const originalText = btn.textContent;
        btn.textContent = '已复制!';
        setTimeout(() => { btn.textContent = originalText; }, 1500);
      } catch (err) {
        btn.textContent = '复制失败';
      }
    });
  });

  // Interactive Mockup Tab Switching
  const tabs = document.querySelectorAll('.mockup-tab');
  const mockupBodies = document.querySelectorAll('.mockup-panel');
  if (tabs.length && mockupBodies.length) {
    tabs.forEach(tab => {
      tab.addEventListener('click', () => {
        tabs.forEach(t => t.classList.remove('active'));
        mockupBodies.forEach(b => b.style.display = 'none');
        
        tab.classList.add('active');
        const targetId = tab.dataset.target;
        const targetBody = document.getElementById(targetId);
        if (targetBody) {
          targetBody.style.display = 'block';
        }
      });
    });
  }

  // Simulated Tag Stream Counter in Mockup
  const streamBody = document.querySelector('#stream-rows');
  if (streamBody) {
    const epcPool = [
      { epc: "E28011910000000000000001", ant: 1, rssi: "-52.0", toi: "VIP 托盘 A-01", count: 142 },
      { epc: "E28011910000000000000002", ant: 2, rssi: "-58.5", toi: "高价值设备 #108", count: 98 },
      { epc: "E28011910000000000000003", ant: 1, rssi: "-64.0", toi: "", count: 410 },
      { epc: "E28011910000000000000004", ant: 3, rssi: "-49.0", toi: "周转箱 G-09", count: 215 },
      { epc: "E28011910000000000000005", ant: 4, rssi: "-71.5", toi: "", count: 64 },
    ];

    setInterval(() => {
      const randomIdx = Math.floor(Math.random() * epcPool.length);
      const item = epcPool[randomIdx];
      item.count += Math.floor(Math.random() * 3) + 1;
      const jitter = (Math.random() * 2 - 1).toFixed(1);
      const newRssi = (parseFloat(item.rssi) + parseFloat(jitter)).toFixed(1);
      
      const rows = streamBody.querySelectorAll('tr');
      if (rows[randomIdx]) {
        const countCell = rows[randomIdx].querySelector('.tag-count');
        const rssiCell = rows[randomIdx].querySelector('.rssi-pill');
        if (countCell) countCell.textContent = item.count;
        if (rssiCell) rssiCell.textContent = `${newRssi} dBm`;
      }
    }, 1200);
  }
});
