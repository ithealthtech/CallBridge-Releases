(function () {
  'use strict';

  const body = document.body;
  const menuButton = document.querySelector('[data-menu-toggle]');
  const navigation = document.querySelector('[data-site-nav]');
  const themeButton = document.querySelector('[data-doc-theme-toggle]');
  const liveRegion = document.querySelector('[data-live-region]');

  function announce(message) {
    if (!liveRegion) return;
    liveRegion.textContent = '';
    window.setTimeout(function () {
      liveRegion.textContent = message;
    }, 20);
  }

  if (menuButton && navigation) {
    menuButton.addEventListener('click', function () {
      const open = navigation.classList.toggle('is-open');
      menuButton.setAttribute('aria-expanded', String(open));
    });

    navigation.addEventListener('click', function (event) {
      if (!event.target.closest('a')) return;
      navigation.classList.remove('is-open');
      menuButton.setAttribute('aria-expanded', 'false');
    });
  }

  if (themeButton) {
    themeButton.addEventListener('click', function () {
      const next = body.dataset.docTheme === 'light' ? 'dark' : 'light';
      body.dataset.docTheme = next;
      themeButton.setAttribute('aria-label', next === 'light' ? 'Use dark documentation theme' : 'Use light documentation theme');
      themeButton.textContent = next === 'light' ? '\u263e' : '\u2600';
      announce('Documentation theme changed to ' + next + '.');
    });
  }

  document.querySelectorAll('[data-copy-target]').forEach(function (button) {
    button.addEventListener('click', async function () {
      const target = document.querySelector(button.dataset.copyTarget);
      if (!target) return;
      const text = target.textContent.trim();

      try {
        await navigator.clipboard.writeText(text);
      } catch (error) {
        const helper = document.createElement('textarea');
        helper.value = text;
        helper.setAttribute('readonly', '');
        helper.className = 'copy-helper';
        document.body.appendChild(helper);
        helper.select();
        document.execCommand('copy');
        helper.remove();
      }

      const original = button.textContent;
      button.textContent = 'Copied';
      announce('Command copied to clipboard.');
      window.setTimeout(function () {
        button.textContent = original;
      }, 1500);
    });
  });

  document.querySelectorAll('[data-filter-list]').forEach(function (container) {
    const input = container.querySelector('[data-filter-input]');
    const items = Array.from(container.querySelectorAll('[data-filter-item]'));
    const empty = container.querySelector('[data-filter-empty]');
    if (!input) return;

    input.addEventListener('input', function () {
      const query = input.value.trim().toLowerCase();
      let visible = 0;
      items.forEach(function (item) {
        const match = !query || item.textContent.toLowerCase().includes(query);
        item.hidden = !match;
        if (match) visible += 1;
      });
      if (empty) empty.classList.toggle('is-visible', !visible);
    });
  });

  document.querySelectorAll('[data-current-year]').forEach(function (element) {
    element.textContent = String(new Date().getFullYear());
  });
})();
