(() => {
  const isEnglish = document.documentElement.lang === 'en' || /\/en(?:\/|$)/.test(location.pathname);
  const page = location.pathname.endsWith('/')
    ? 'index.html'
    : (location.pathname.split('/').pop() || 'index.html');
  const toggle = document.querySelector('.menu-toggle');
  const nav = document.querySelector('.topbar nav');
  if (toggle && nav) {
    const closeNav = () => {
      nav.classList.remove('open');
      toggle.setAttribute('aria-expanded', 'false');
    };
    toggle.setAttribute('aria-label', isEnglish ? 'Toggle navigation' : '切换导航');
    toggle.setAttribute('aria-expanded', 'false');
    toggle.addEventListener('click', () => {
      const open = nav.classList.toggle('open');
      toggle.setAttribute('aria-expanded', String(open));
    });
    nav.querySelectorAll('a').forEach(link => link.addEventListener('click', closeNav));
    const closeIfOutside = event => {
      const target = event.target;
      if (target instanceof Node && (nav.contains(target) || toggle.contains(target))) return;
      closeNav();
    };
    document.addEventListener('click', closeIfOutside, true);
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape') closeNav();
    });
  }

  // Dropdown manual menu logic
  const manualMenu = document.querySelector('.nav-dropdown');
  const manualToggle = manualMenu?.querySelector('.nav-dropdown-toggle');
  if (manualMenu && manualToggle) {
    const closeManualMenu = () => {
      manualMenu.classList.remove('open');
      manualToggle.setAttribute('aria-expanded', 'false');
    };
    manualToggle.addEventListener('click', event => {
      event.stopPropagation();
      const open = manualMenu.classList.toggle('open');
      manualToggle.setAttribute('aria-expanded', String(open));
    });
    manualMenu.querySelectorAll('a').forEach(link => link.addEventListener('click', closeManualMenu));
    document.addEventListener('click', event => {
      const target = event.target;
      if (!(target instanceof Node) || !manualMenu.contains(target)) closeManualMenu();
    }, true);
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape') closeManualMenu();
    });
  }

  // Language Toggle Button (Clean single-line placement)
  const navContainer = document.querySelector('.topbar nav');
  if (navContainer && !navContainer.querySelector('.language-toggle')) {
    const languageToggle = document.createElement('a');
    languageToggle.className = 'language-toggle';
    languageToggle.href = `${isEnglish ? '../' : 'en/'}${page}${location.hash}`;
    languageToggle.textContent = isEnglish ? '中文' : 'EN';
    languageToggle.setAttribute('aria-label', isEnglish ? '切换到中文' : 'Switch to English');
    languageToggle.title = isEnglish ? '切换到中文' : 'Switch to English';
    const github = navContainer.querySelector('.github');
    navContainer.insertBefore(languageToggle, github || null);
  }

  // Highlight active links
  document.querySelectorAll('.topbar nav a').forEach(link => {
    const href = link.getAttribute('href');
    if (href && href.split('#')[0] === page) link.classList.add('active');
  });
  if (manualMenu && manualToggle && manualMenu.querySelector('a.active')) {
    manualToggle.classList.add('active');
  }

  // Code Copy
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

  // Scrollspy for side nav
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
