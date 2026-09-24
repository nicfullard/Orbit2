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

// Task form: the Parent task picker only offers tasks on the chosen project - or, for a standalone task,
// standalone tasks in the chosen department (spec §6.15). The department source may be a select or a hidden input.
function orbitFilterParents() {
  var parents = document.querySelector('select.js-parent-select');
  if (!parents) return;
  var projectEl = document.querySelector(parents.dataset.projectSource || '#Form_ProjectId');
  var deptEl = document.querySelector(parents.dataset.departmentSource || '#Form_DepartmentId');
  var project = projectEl ? projectEl.value : '';
  var dept = deptEl ? deptEl.value : '';
  var selectedHidden = false;
  Array.prototype.forEach.call(parents.options, function (opt) {
    if (!opt.value) return;
    var ownProject = opt.dataset.project || '';
    var ownDept = opt.dataset.department || '';
    var show = project ? ownProject === project : (!ownProject && (!dept || ownDept === dept));
    opt.hidden = !show;
    opt.disabled = !show;
    if (!show && opt.selected) selectedHidden = true;
  });
  if (selectedHidden) parents.value = '';
}
document.addEventListener('change', function (e) {
  var el = e.target;
  if (!el || !el.classList) return;
  if (el.classList.contains('js-project-select') || el.classList.contains('js-department-select')) orbitFilterParents();
});
document.addEventListener('DOMContentLoaded', orbitFilterParents);

// User form (Admin > Users): a directory (LDAP) user has no Orbit password, so the temporary-password field is
// hidden and disabled for them (a disabled input is neither validated nor submitted).
function orbitToggleLocalPassword(select) {
  var isLocal = select.value !== 'Ldap';
  document.querySelectorAll('.js-local-password').forEach(function (el) {
    el.hidden = !isLocal;
    el.querySelectorAll('input').forEach(function (input) { input.disabled = !isLocal; });
  });
}
document.addEventListener('change', function (e) {
  var el = e.target;
  if (el && el.classList && el.classList.contains('js-auth-source')) orbitToggleLocalPassword(el);
});
document.addEventListener('DOMContentLoaded', function () {
  document.querySelectorAll('select.js-auth-source').forEach(orbitToggleLocalPassword);
});

// User and API key forms (Admin): a role with a permission at Department scope needs a department, so "(none)" is
// withheld while such a role is chosen. The server checks regardless.
function orbitApplyRoleDepartmentRule(select) {
  var opt = select.options[select.selectedIndex];
  var requires = !!opt && opt.dataset.requiresDepartment === 'true';
  var dept = document.querySelector(select.dataset.departmentTarget || '#Form_DepartmentId');
  if (!dept) return;
  Array.prototype.forEach.call(dept.options, function (o) {
    if (o.value) return;
    o.hidden = requires;
    o.disabled = requires;
  });
  dept.required = requires;
  var hint = select.dataset.departmentHint ? document.querySelector(select.dataset.departmentHint) : null;
  if (hint) {
    hint.textContent = requires
      ? 'Required: this role has permissions scoped to a department.'
      : 'Optional for this role: a home department, used as the default for new work.';
  }
}
document.addEventListener('change', function (e) {
  var el = e.target;
  if (el && el.classList && el.classList.contains('js-role-select')) orbitApplyRoleDepartmentRule(el);
});
document.addEventListener('DOMContentLoaded', function () {
  document.querySelectorAll('select.js-role-select').forEach(orbitApplyRoleDepartmentRule);
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

// Inline due-date control: a date input fires "change" on every keystroke that yields a valid date, so submitting
// straight away would cut keyboard entry short. Wait for a pause in typing; Enter or leaving the field submits at once.
(function () {
  var pending = null;
  function submit(el) {
    if (pending) { clearTimeout(pending); pending = null; }
    if (el.form && el.value !== el.dataset.original) {
      el.dataset.original = el.value;
      el.form.requestSubmit ? el.form.requestSubmit() : el.form.submit();
    }
  }
  function isDelayed(el) { return el && el.classList && el.classList.contains('js-autosubmit-delayed'); }
  document.addEventListener('change', function (e) {
    var el = e.target;
    if (!isDelayed(el)) return;
    if (pending) clearTimeout(pending);
    pending = setTimeout(function () { submit(el); }, 700);
  });
  document.addEventListener('focusout', function (e) { if (isDelayed(e.target)) submit(e.target); });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && isDelayed(e.target)) { e.preventDefault(); submit(e.target); }
  });
})();

// Asset form (spec §6.19): the type and location lists follow the managing department, the property fields follow the
// type (re-rendered by the page's Properties handler, which also says what a change of type carries over), and "Disposed on"
// shows only for a disposed asset. Holders are chosen with the people picker below.
(function () {
  function assetForm(el) { return el && el.closest ? el.closest('form.js-asset-form') : null; }
  function withParam(url, name, value) {
    return url + (url.indexOf('?') < 0 ? '?' : '&') + name + '=' + encodeURIComponent(value || '');
  }
  function option(value, text, selected) {
    var o = document.createElement('option');
    o.value = value;
    o.textContent = text;
    if (selected) o.selected = true;
    return o;
  }
  function loadProperties(form) {
    var type = form.querySelector('.js-asset-type');
    var target = form.querySelector('.js-asset-properties');
    if (!type || !target || !form.dataset.propertiesUrl) return;
    fetch(withParam(form.dataset.propertiesUrl, 'typeId', type.value), { credentials: 'same-origin' })
      .then(function (r) { return r.ok ? r.text() : null; })
      .then(function (html) { if (html !== null) target.innerHTML = html; });
  }
  function fillTypes(select, types) {
    var current = select.value;
    select.innerHTML = '';
    select.appendChild(option('', '- choose -', false));
    var groups = {};
    types.forEach(function (t) {
      var parent = select;
      if (t.category) {
        if (!groups[t.category]) {
          groups[t.category] = document.createElement('optgroup');
          groups[t.category].label = t.category;
          select.appendChild(groups[t.category]);
        }
        parent = groups[t.category];
      }
      parent.appendChild(option(t.id, t.name, t.id === current));
    });
  }
  function fillLocations(select, locations) {
    var current = select.value;
    select.innerHTML = '';
    select.appendChild(option('', '- none -', false));
    locations.forEach(function (l) { select.appendChild(option(l.id, l.name, l.id === current)); });
  }
  function loadChoices(form) {
    var dept = form.querySelector('.js-asset-department');
    var types = form.querySelector('.js-asset-type');
    var locations = form.querySelector('.js-asset-location');
    if (!dept || !types || !form.dataset.choicesUrl) return;
    if (!dept.value) {
      fillTypes(types, []);
      if (locations) fillLocations(locations, []);
      loadProperties(form);
      return;
    }
    fetch(withParam(form.dataset.choicesUrl, 'departmentId', dept.value), { credentials: 'same-origin' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data) return;
        fillTypes(types, data.types || []);
        if (locations) fillLocations(locations, data.locations || []);
        loadProperties(form);
      });
  }
  function toggleDisposed(form) {
    var status = form.querySelector('.js-asset-status');
    var box = form.querySelector('.js-disposed-on');
    if (status && box) box.classList.toggle('d-none', status.value !== 'Disposed');
  }
  document.addEventListener('change', function (e) {
    var el = e.target;
    var form = assetForm(el);
    if (form) {
      if (el.classList.contains('js-asset-type')) loadProperties(form);
      else if (el.classList.contains('js-asset-department')) loadChoices(form);
      else if (el.classList.contains('js-asset-status')) toggleDisposed(form);
    }
    // Asset type properties: the options box is for a Choice property only.
    if (el.classList && el.classList.contains('js-property-type')) {
      var box = el.closest('form') && el.closest('form').querySelector('.js-property-options');
      if (box) box.classList.toggle('d-none', el.value !== 'Choice');
    }
  });
  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('form.js-asset-form').forEach(toggleDisposed);
  });
})();

// People picker (spec §6.19): the people chosen as chips, and a type-ahead search against the server (data-search-url?q=,
// at most 20 people, so it works however many people Orbit has). In "multi" mode each chip carries a hidden input the form
// posts; in "submit" mode picking someone fills the picker's hidden field and posts its form at once (the asset page's
// "Assign someone"). Keyboard: Up/Down move through the results, Enter picks, Escape closes. Adds and removes are announced.
(function () {
  function withParam(url, name, value) {
    return url + (url.indexOf('?') < 0 ? '?' : '&') + name + '=' + encodeURIComponent(value || '');
  }
  function text(tag, className, value) {
    var el = document.createElement(tag);
    if (className) el.className = className;
    if (value) el.textContent = value;
    return el;
  }
  function setup(root) {
    if (root.dataset.pickerReady) return;
    root.dataset.pickerReady = '1';
    var input = root.querySelector('.js-picker-input');
    var list = root.querySelector('.js-picker-results');
    var chips = root.querySelector('.js-picker-chips');
    var status = root.querySelector('.js-picker-status');
    var submitMode = root.dataset.mode === 'submit';
    var timer = null, items = [], active = -1, seq = 0;

    function say(message) { if (status) status.textContent = message; }
    function chosenIds() {
      return Array.prototype.map.call(root.querySelectorAll('.person-chip'), function (c) { return c.dataset.id; });
    }
    function updateEmpty() {
      var empty = root.querySelector('.js-picker-empty');
      if (empty) empty.classList.toggle('d-none', root.querySelectorAll('.person-chip').length > 0);
    }
    function close() {
      list.classList.add('d-none');
      list.innerHTML = '';
      input.setAttribute('aria-expanded', 'false');
      input.removeAttribute('aria-activedescendant');
      items = [];
      active = -1;
    }
    function highlight(index) {
      var options = list.querySelectorAll('[role="option"]');
      if (!options.length) return;
      active = (index + options.length) % options.length;
      Array.prototype.forEach.call(options, function (o, i) {
        o.classList.toggle('active', i === active);
        o.setAttribute('aria-selected', i === active ? 'true' : 'false');
      });
      input.setAttribute('aria-activedescendant', options[active].id);
      options[active].scrollIntoView({ block: 'nearest' });
    }
    function render(people, query) {
      list.innerHTML = '';
      items = people;
      active = -1;
      if (!people.length) {
        list.appendChild(text('li', 'list-group-item small text-muted', 'No one matches "' + query + '".'));
      }
      people.forEach(function (p, i) {
        var li = text('li', 'list-group-item list-group-item-action py-1 small');
        li.id = root.id + '-option-' + i;
        li.setAttribute('role', 'option');
        li.setAttribute('aria-selected', 'false');
        li.dataset.index = i;
        li.appendChild(text('span', 'fw-semibold', p.name));
        var detail = [p.email, p.department].filter(Boolean).join(' · ');
        if (detail) li.appendChild(text('span', 'text-muted ms-2', detail));
        list.appendChild(li);
      });
      list.classList.remove('d-none');
      input.setAttribute('aria-expanded', 'true');
      if (people.length) highlight(0);
    }
    function addChip(p) {
      var chip = text('span', 'badge rounded-pill text-bg-light border person-chip');
      chip.dataset.id = p.id;
      chip.dataset.name = p.name;
      chip.appendChild(text('span', 'fw-semibold', p.name));
      if (p.department) chip.appendChild(text('span', 'text-muted fw-normal ms-1', p.department));
      var hidden = document.createElement('input');
      hidden.type = 'hidden';
      hidden.name = root.dataset.fieldName;
      hidden.value = p.id;
      chip.appendChild(hidden);
      var remove = text('button', 'btn-close ms-1 js-picker-remove');
      remove.type = 'button';
      remove.setAttribute('aria-label', 'Remove ' + p.name);
      chip.appendChild(remove);
      chips.insertBefore(chip, root.querySelector('.js-picker-empty'));
      updateEmpty();
    }
    function choose(p) {
      if (!p) return;
      if (submitMode) {
        var field = root.querySelector('input[type="hidden"][name="' + root.dataset.fieldName + '"]');
        var form = root.closest('form');
        if (!field || !form) return;
        field.value = p.id;
        input.disabled = true;
        say('Assigning to ' + p.name);
        if (form.requestSubmit) form.requestSubmit(); else form.submit();
        return;
      }
      addChip(p);
      input.value = '';
      close();
      say(p.name + ' added.');
      input.focus();
    }
    function search() {
      var query = input.value.trim();
      if (!query) { close(); return; }
      var mine = ++seq;
      fetch(withParam(root.dataset.searchUrl, 'q', query), { credentials: 'same-origin', headers: { Accept: 'application/json' } })
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(function (people) {
          if (mine !== seq || input.value.trim() !== query) return; // a newer search is on its way
          var taken = chosenIds();
          render(people.filter(function (p) { return taken.indexOf(p.id) < 0; }), query);
        })
        .catch(function () { close(); });
    }

    input.addEventListener('input', function () {
      clearTimeout(timer);
      timer = setTimeout(search, 200);
    });
    input.addEventListener('keydown', function (e) {
      var open = !list.classList.contains('d-none');
      if (e.key === 'ArrowDown') { e.preventDefault(); if (open) highlight(active + 1); else search(); }
      else if (e.key === 'ArrowUp') { if (open) { e.preventDefault(); highlight(active - 1); } }
      else if (e.key === 'Enter') { e.preventDefault(); if (open && active >= 0) choose(items[active]); }
      else if (e.key === 'Escape') { if (open) { e.preventDefault(); close(); } }
    });
    input.addEventListener('blur', function () { setTimeout(close, 150); });
    list.addEventListener('mousedown', function (e) {
      var option = e.target.closest('[role="option"]');
      if (!option) return;
      e.preventDefault(); // keep focus in the box, so blur doesn't close the list first
      choose(items[+option.dataset.index]);
    });
    if (chips) {
      chips.addEventListener('click', function (e) {
        var button = e.target.closest('.js-picker-remove');
        if (!button) return;
        var chip = button.closest('.person-chip');
        say(chip.dataset.name + ' removed.');
        chip.remove();
        updateEmpty();
        input.focus();
      });
    }
  }
  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.js-person-picker').forEach(setup);
  });
})();
