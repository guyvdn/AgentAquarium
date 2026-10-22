// Reads the Schools list and finds the working root with the most subagents; with window.__hover
// set (HOVER=1), points window.__follow at it so its tooltip shows.
// Returns "rows|needsYou|followed" for capture.sh to decide whether the scene is worth shooting.
(() => {
  const rows = [...document.querySelectorAll('#schools li[data-id]')];
  const roots = [];
  for (const li of rows) {
    const name = li.querySelector('.name')?.firstChild?.textContent.trim();
    if (!name) continue; // a row without a label
    if (!li.classList.contains('child'))
      roots.push({ name, status: li.querySelector('.meta')?.innerText ?? '', kids: 0 });
    else if (roots.length) roots[roots.length - 1].kids++;
  }
  let best = null;
  let most = -1;
  for (const r of roots)
    if (/working/.test(r.status) && r.kids > most && window.__labels?.[r.name]) {
      best = r.name;
      most = r.kids;
    }
  window.__follow = window.__hover ? best : null;
  const needsYou = (document.getElementById('schools')?.innerText ?? '').includes('needs you');
  return `${rows.length}|${needsYou}|${best}`;
})();
