(() => {
  const isEnglish = document.documentElement.lang === 'en' || /\/en(?:\/|$)/.test(location.pathname);
  const page = location.pathname.endsWith('/')
    ? 'index.html'
    : (location.pathname.split('/').pop() || 'index.html');
  const toggle = document.querySelector('.menu-toggle');
  const nav = document.querySelector('.topbar nav');
  if (toggle && nav) {
    toggle.setAttribute('aria-label', isEnglish ? 'Toggle navigation' : '切换导航');
    toggle.setAttribute('aria-expanded', 'false');
    toggle.addEventListener('click', () => {
      const open = nav.classList.toggle('open');
      toggle.setAttribute('aria-expanded', String(open));
    });
  }

  const languageToggle = document.createElement('a');
  languageToggle.className = 'language-toggle';
  languageToggle.href = `${isEnglish ? '../' : 'en/'}${page}${location.hash}`;
  languageToggle.textContent = isEnglish ? '中文' : 'EN';
  languageToggle.setAttribute('aria-label', isEnglish ? '切换到中文' : 'Switch to English');
  languageToggle.title = isEnglish ? '切换到中文' : 'Switch to English';
  const languageHost = document.querySelector('.topbar')
    || document.querySelector('.reference-topbar .topbar-links')
    || document.querySelector('.reference-topbar');
  const github = languageHost?.querySelector('.github');
  languageHost?.insertBefore(languageToggle, github || null);

  document.querySelectorAll('.topbar nav a').forEach(link => {
    if (link.getAttribute('href').split('#')[0] === page) link.classList.add('active');
  });

  document.querySelectorAll('.copy-code, .copy-btn').forEach(btn => {
    btn.addEventListener('click', async () => {
      const pre = btn.parentElement.querySelector('pre') || btn.nextElementSibling;
      const code = pre?.innerText;
      if (!code) return;
      try {
        await navigator.clipboard.writeText(code.trim());
        const originalText = btn.textContent;
        btn.textContent = isEnglish ? 'Copied' : '已复制';
        setTimeout(() => btn.textContent = originalText, 1500);
      } catch (err) {
        btn.textContent = isEnglish ? 'Copy manually' : '复制失败';
      }
    });
  });

  const navLinks = Array.from(document.querySelectorAll('.side-nav a[href^="#"]'));
  const sections = Array.from(document.querySelectorAll('.doc-content section[id]'));
  if (navLinks.length && sections.length) {
    window.addEventListener('scroll', () => {
      const scrollPos = window.scrollY + 120;
      let currentId = '';
      sections.forEach(sec => {
        if (sec.offsetTop <= scrollPos) currentId = sec.id;
      });
      if (currentId) {
        navLinks.forEach(link => {
          link.classList.toggle('active', link.getAttribute('href') === `#${currentId}`);
        });
      }
    }, { passive: true });
  }
})();
