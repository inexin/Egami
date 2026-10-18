// Adds a version picker to the navigation bar, and a notice on pages of an older major version.
// The site keeps one folder per major version (v1/, v2/, ...) next to versions.json, newest first.
export default {
  start: async () => {
    const rel = document.querySelector('meta[name="docfx:rel"]')?.getAttribute('content') ?? './';
    const versionRoot = new URL(rel, window.location.href);
    const siteRoot = new URL('../', versionRoot);
    let versions;
    try {
      const response = await fetch(new URL('versions.json', siteRoot));
      if (!response.ok) {
        return;
      }
      versions = await response.json();
    } catch {
      return;
    }

    const current = versions.find(v => new URL(v.path, siteRoot).href === versionRoot.href);
    if (!current) {
      return;
    }

    const page = window.location.href.substring(versionRoot.href.length);
    const select = document.createElement('select');
    select.className = 'form-select form-select-sm egami-version';
    select.setAttribute('aria-label', 'Documentation version');
    for (const version of versions) {
      const option = new Option(version.name, version.path, false, version === current);
      select.add(option);
    }
    select.addEventListener('change', async () => {
      const target = new URL(select.value, siteRoot);
      const samePage = new URL(page, target);
      try {
        const response = await fetch(samePage, { method: 'HEAD' });
        window.location.href = response.ok ? samePage.href : target.href;
      } catch {
        window.location.href = target.href;
      }
    });
    const nav = document.querySelector('header nav form.icons') ?? document.querySelector('header nav');
    nav?.prepend(select);

    if (current !== versions[0]) {
      const notice = document.createElement('div');
      notice.className = 'alert alert-warning m-0 rounded-0 text-center';
      const link = document.createElement('a');
      link.href = new URL(versions[0].path, siteRoot).href;
      link.textContent = `version ${versions[0].name}`;
      notice.append(`These are the docs for Egami ${current.name}. The newest are for `, link, '.');
      document.querySelector('header')?.after(notice);
    }
  },
};
