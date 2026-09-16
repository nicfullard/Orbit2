// Inline status control: submit the enclosing form as soon as a new status is picked.
document.addEventListener('change', function (e) {
  var el = e.target;
  if (el && el.classList && el.classList.contains('js-autosubmit') && el.form) {
    el.form.requestSubmit ? el.form.requestSubmit() : el.form.submit();
  }
});

// "Select all" checkbox for bulk planning.
document.addEventListener('change', function (e) {
  var el = e.target;
  if (el && el.classList && el.classList.contains('js-select-all')) {
    document.querySelectorAll('input.js-select-item').forEach(function (cb) { cb.checked = el.checked; });
  }
});

// Confirmations
document.addEventListener('submit', function (e) {
  var form = e.target;
  if (form && form.dataset && form.dataset.confirm) {
    if (!window.confirm(form.dataset.confirm)) e.preventDefault();
  }
});

// Copy-to-clipboard buttons
document.addEventListener('click', function (e) {
  var btn = e.target.closest('[data-copy-target]');
  if (!btn) return;
  var target = document.querySelector(btn.dataset.copyTarget);
  if (!target) return;
  var text = target.value !== undefined ? target.value : target.textContent;
  navigator.clipboard.writeText(text.trim()).then(function () {
    var old = btn.textContent;
    btn.textContent = 'Copied';
    setTimeout(function () { btn.textContent = old; }, 1500);
  });
});

// Task / recurring-task forms (System Admin): picking a project defaults the department to the project's own,
// and the assignee list narrows to people in the chosen department (System Admins are always assignable).
function orbitFilterAssignees(deptSelect) {
  var assignees = document.querySelector(deptSelect.dataset.assigneeTarget || '#Form_AssigneeId');
  if (!assignees) return;
  var dept = deptSelect.value;
  var selectedHidden = false;
  Array.prototype.forEach.call(assignees.options, function (opt) {
    var own = opt.dataset.department || '';
    var show = !dept || !own || own === dept;
    opt.hidden = !show;
    opt.disabled = !show;
    if (!show && opt.selected) selectedHidden = true;
  });
  if (selectedHidden) assignees.value = '';
}
document.addEventListener('change', function (e) {
  var el = e.target;
  if (!el || !el.classList) return;
  if (el.classList.contains('js-project-select')) {
    var deptSelect = document.querySelector(el.dataset.departmentTarget || '#Form_DepartmentId');
    if (deptSelect && deptSelect.tagName === 'SELECT') {
      var opt = el.options[el.selectedIndex];
      deptSelect.value = opt && opt.dataset.department ? opt.dataset.department : '';
      orbitFilterAssignees(deptSelect);
    }
  } else if (el.classList.contains('js-department-select')) {
    orbitFilterAssignees(el);
  }
});
document.addEventListener('DOMContentLoaded', function () {
  document.querySelectorAll('select.js-department-select').forEach(orbitFilterAssignees);
});

// Light / dark theme toggle (navbar icon). The chosen theme is stored in localStorage and re-applied by the
// inline script in _Layout.cshtml before first paint; this just flips it and keeps the button's label current.
(function () {
  var STORAGE_KEY = 'orbit-theme';
  function currentTheme() {
    return document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'light';
  }
  function applyTheme(theme) {
    document.documentElement.setAttribute('data-bs-theme', theme);
    var label = theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme';
    document.querySelectorAll('.js-theme-toggle').forEach(function (btn) {
      btn.title = label;
      btn.setAttribute('aria-label', label);
    });
  }
  document.addEventListener('DOMContentLoaded', function () { applyTheme(currentTheme()); });
  document.addEventListener('click', function (e) {
    var btn = e.target.closest('.js-theme-toggle');
    if (!btn) return;
    var next = currentTheme() === 'dark' ? 'light' : 'dark';
    applyTheme(next);
    try { localStorage.setItem(STORAGE_KEY, next); } catch (err) { /* storage unavailable: theme lasts for this page only */ }
  });
})();

// Task clock (task details page). Ticks the elapsed-time display every second, and when the user leaves the page
// while the clock is running, stops it via navigator.sendBeacon so the time is logged (see §6.10 of the spec).
(function () {
  var clock = document.querySelector('.js-clock[data-clock-started]');
  if (!clock) return;
  var startedAt = new Date(clock.dataset.clockStarted).getTime();
  var display = clock.querySelector('.js-clock-elapsed');
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function tick() {
    var s = Math.max(0, Math.floor((Date.now() - startedAt) / 1000));
    if (display) display.textContent = pad(Math.floor(s / 3600)) + ':' + pad(Math.floor((s % 3600) / 60)) + ':' + pad(s % 60);
  }
  tick();
  setInterval(tick, 1000);

  // Posting a comment, logging time or changing status submits a form that comes straight back to this page,
  // which is not "leaving": skip the beacon for those. A cancelled confirm() leaves the flag alone.
  var stayingOnPage = false;
  document.addEventListener('submit', function (e) {
    if (!e.defaultPrevented) stayingOnPage = true;
  });
  window.addEventListener('pagehide', function () {
    if (stayingOnPage || !navigator.sendBeacon) return;
    var beacon = document.querySelector('form.js-clock-beacon');
    if (!beacon) return;
    navigator.sendBeacon(beacon.action, new FormData(beacon));
  });
  // Coming back via the back/forward cache would show a clock that was already stopped: reload instead.
  window.addEventListener('pageshow', function (e) { if (e.persisted) location.reload(); });
})();
