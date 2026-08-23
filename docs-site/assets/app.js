(() => {
  const toggle = document.querySelector('.menu-toggle');
  const nav = document.querySelector('.topbar nav');
  if (toggle && nav) {
    toggle.addEventListener('click', () => {
      nav.classList.toggle('open');
    });
  }

  document.querySelectorAll('.copy-code, .copy-btn').forEach(btn => {
    btn.addEventListener('click', async () => {
      const pre = btn.parentElement.querySelector('pre') || btn.nextElementSibling;
      const code = pre?.innerText;
      if (!code) return;
      try {
        await navigator.clipboard.writeText(code.trim());
        const originalText = btn.textContent;
        btn.textContent = '已复制';
        setTimeout(() => btn.textContent = originalText, 1500);
      } catch (err) {
        btn.textContent = '复制失败';
      }
    });
  });

  // Highlight active side navigation on scroll
  const navLinks = Array.from(document.querySelectorAll('.side-nav a[href^="#"]'));
  const sections = Array.from(document.querySelectorAll('.doc-content section[id]'));
  if (navLinks.length && sections.length) {
    window.addEventListener('scroll', () => {
      const scrollPos = window.scrollY + 120;
      let currentId = '';
      sections.forEach(sec => {
        if (sec.offsetTop <= scrollPos) {
          currentId = sec.id;
        }
      });
      if (currentId) {
        navLinks.forEach(link => {
          link.classList.toggle('active', link.getAttribute('href') === `#${currentId}`);
        });
      }
    }, { passive: true });
  }
})();
