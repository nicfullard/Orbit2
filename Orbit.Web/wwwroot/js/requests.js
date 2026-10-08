// Requests (spec §6.20). On the pages that fill in a form the fields are shown one at a time: Next checks the step before moving on,
// Back returns, and a last step reviews every answer (each with a Change link back to its step) before the form is posted. Without
// this script the page shows every field at once with one button, and the server checks the answers either way. The builder pages
// use the same file for their small conveniences: showing the settings a chosen type or kind needs, inserting a token into a template,
// and marking a web-page step done when its link is opened.
(function () {
  function textOf(el) { return el ? el.textContent.replace(/\s+/g, ' ').trim() : ''; }

  // What the review shows for one step's answer: '' when it has none.
  function answerOf(step) {
    if (step.dataset.files) {
      var files = step.querySelector('.js-files');
      return files && files.files.length ? Array.prototype.map.call(files.files, function (f) { return f.name; }).join('\n') : '';
    }
    var parts = [];
    var checked = step.querySelector('input[type="radio"]:checked');
    if (checked) {
      var label = step.querySelector('label[for="' + checked.id + '"]');
      parts.push([textOf(label.querySelector('.js-choice-label')), textOf(label.querySelector('.js-choice-detail'))].filter(Boolean).join(' - '));
    }
    step.querySelectorAll('.js-picker-chip').forEach(function (chip) { parts.push(chip.dataset.name); });
    var select = step.querySelector('select');
    if (select && select.value) parts.push(select.options[select.selectedIndex].text);
    step.querySelectorAll('textarea, input[type="text"], input[type="number"], input[type="date"]').forEach(function (f) {
      if (!f.disabled && f.value.trim()) parts.push(f.value.trim());
    });
    return parts.join('\n');
  }

  function setupFlow(form) {
    var steps = Array.prototype.slice.call(form.querySelectorAll('.js-step'));
    var review = form.querySelector('.js-review-step');
    var note = form.querySelector('.js-review-step-note');
    var pages = steps.concat([review]);
    var back = form.querySelector('.js-back');
    var next = form.querySelector('.js-next');
    var submit = form.querySelector('.js-submit');
    var counter = form.querySelector('.js-step-counter');
    var bar = form.querySelector('.js-step-bar');
    var progress = bar.parentElement;
    var stepper = document.querySelector('.js-request-steps');
    var current = 0;

    form.classList.add('is-wizard');
    form.noValidate = true; // each step is checked as it is left, and every step again on submit

    // Checks one step and shows why it can't be left; returns whether it is fine.
    function check(step) {
      var message = '';
      if (step.dataset.pickerRequired && !step.querySelector('.js-picker-chip')) message = 'Choose one from the list.';
      if (!message && step.dataset.filesRequired) {
        var files = step.querySelector('.js-files');
        if (!files || !files.files.length) message = 'Attach at least one file.';
      }
      if (!message) {
        var fields = step.querySelectorAll('input, textarea, select');
        for (var i = 0; i < fields.length; i++) {
          if (fields[i].willValidate && !fields[i].checkValidity()) { message = fields[i].validationMessage || 'Check this answer.'; break; }
        }
      }
      var error = step.querySelector('.js-step-error');
      if (error) error.textContent = message;
      step.querySelectorAll('input, textarea, select').forEach(function (f) {
        if (f.type !== 'hidden') f.setAttribute('aria-invalid', message ? 'true' : 'false');
      });
      return !message;
    }

    function markStepper(onReview) {
      if (!stepper) return;
      stepper.querySelectorAll('li').forEach(function (li) {
        var n = +li.dataset.step, now = onReview ? 3 : 2;
        li.classList.toggle('is-done', n < now);
        li.classList.toggle('is-current', n === now);
        if (n === now) li.setAttribute('aria-current', 'step'); else li.removeAttribute('aria-current');
        li.querySelector('.request-step-number').textContent = n < now ? '✓' : String(n);
      });
    }

    function buildReview() {
      var list = review.querySelector('.js-review');
      list.innerHTML = '';
      steps.forEach(function (step, index) {
        var legend = step.querySelector('legend').cloneNode(true);
        legend.querySelectorAll('.text-muted').forEach(function (n) { n.remove(); });
        var dt = document.createElement('dt');
        dt.textContent = textOf(legend) + ' ';
        var change = document.createElement('button');
        change.type = 'button';
        change.className = 'btn btn-link';
        change.textContent = 'Change';
        change.setAttribute('aria-label', 'Change: ' + textOf(legend));
        change.addEventListener('click', function () { show(index, true); });
        dt.appendChild(change);
        var dd = document.createElement('dd');
        var answer = answerOf(step);
        if (answer) dd.textContent = answer;
        else { dd.textContent = step.dataset.files ? 'No files' : 'No answer'; dd.className = 'text-muted fst-italic'; }
        list.appendChild(dt);
        list.appendChild(dd);
      });
    }

    function show(index, focus) {
      current = index;
      var onReview = index === pages.length - 1;
      pages.forEach(function (p, i) { p.hidden = i !== index; });
      if (note) note.hidden = !onReview;
      back.hidden = index === 0;
      next.hidden = onReview;
      submit.hidden = !onReview;
      counter.textContent = onReview ? 'Check and send' : 'Step ' + (index + 1) + ' of ' + (pages.length - 1);
      var percent = Math.round(index / (pages.length - 1) * 100);
      bar.style.width = percent + '%';
      progress.setAttribute('aria-valuenow', String(percent));
      markStepper(onReview);
      if (onReview) buildReview();
      if (focus) {
        var heading = onReview ? review.querySelector('h2') : pages[index].querySelector('legend');
        if (heading) heading.focus();
      }
    }

    next.addEventListener('click', function () {
      if (current >= pages.length - 1) return; // already on the review
      if (check(pages[current])) show(current + 1, true);
      else {
        var bad = pages[current].querySelector('[aria-invalid="true"]');
        if (bad && bad.focus) bad.focus();
      }
    });
    back.addEventListener('click', function () { if (current > 0) show(current - 1, true); });

    // Once a step shows why it can't be left, answering it clears the message (and a still-wrong answer says so again).
    function recheck(e) {
      var step = e.target.closest && e.target.closest('.js-step');
      var error = step && step.querySelector('.js-step-error');
      if (error && error.textContent) check(step);
    }
    form.addEventListener('input', recheck);
    form.addEventListener('change', recheck);
    form.addEventListener('click', function (e) { if (e.target.closest('.js-picker-remove')) setTimeout(function () { recheck(e); }, 0); });

    // Enter moves on from a one-line answer, as it would submit a one-field form - not from a text area, nor inside a picker.
    form.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter' || e.defaultPrevented) return;
      var t = e.target;
      if (t.tagName === 'TEXTAREA' || t.tagName === 'BUTTON' || t.type === 'file' || t.closest('.js-picker')) return;
      if (t.tagName === 'INPUT' || t.tagName === 'SELECT') {
        e.preventDefault();
        if (!next.hidden) next.click();
      }
    });

    form.addEventListener('submit', function (e) {
      for (var i = 0; i < steps.length; i++) {
        if (!check(steps[i])) { e.preventDefault(); show(i, true); return; }
      }
      submit.disabled = true;
      submit.textContent = submit.dataset.submitting || 'Logging...';
    });

    // After a refused post, open the first field the server marked; otherwise start at the beginning (or the review, with no fields).
    var marked = steps.findIndex(function (s) { return s.dataset.hasError; });
    show(marked >= 0 ? marked : steps.length === 0 ? pages.length - 1 : 0, marked >= 0);
  }

  // The builder pages: a select with data-syncs="type" shows the blocks whose data-for-type lists its value (and so for kind,
  // source, where and action), hiding and disabling the others so their inputs don't post.
  function sync(select) {
    var name = select.dataset.syncs;
    var scope = select.form || document;
    scope.querySelectorAll('[data-for-' + name + ']').forEach(function (el) {
      var on = el.getAttribute('data-for-' + name).split(' ').indexOf(select.value) >= 0;
      el.hidden = !on;
      el.querySelectorAll('input, select, textarea').forEach(function (f) { f.disabled = !on; });
    });
  }

  // The token picker: choosing a value inserts its token where the cursor is in the target box.
  function insertToken(select) {
    var target = document.getElementById(select.dataset.target);
    var token = select.value;
    select.value = '';
    if (!target || !token) return;
    var start = target.selectionStart == null ? target.value.length : target.selectionStart;
    var end = target.selectionEnd == null ? start : target.selectionEnd;
    target.value = target.value.slice(0, start) + token + target.value.slice(end);
    target.focus();
    target.setSelectionRange(start + token.length, start + token.length);
  }

  document.addEventListener('change', function (e) {
    if (e.target.matches && e.target.matches('.js-sync')) sync(e.target);
    if (e.target.matches && e.target.matches('.js-token-picker')) insertToken(e.target);
  });

  // Opening a web-page step's link (in a new tab) marks the step done a moment later, once the new tab has been opened.
  document.addEventListener('click', function (e) {
    var link = e.target.closest && e.target.closest('.js-url-open');
    if (!link) return;
    var done = link.parentElement.querySelector('.js-url-done');
    if (done) setTimeout(function () { if (done.requestSubmit) done.requestSubmit(); else done.submit(); }, 400);
  });

  // A confirmation on one button of a form with several (Decline beside Approve): site.js only knows data-confirm on the form.
  document.addEventListener('click', function (e) {
    var button = e.target.closest && e.target.closest('button[data-confirm]');
    if (button && button.form && !window.confirm(button.dataset.confirm)) e.preventDefault();
  });

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.js-request-flow').forEach(setupFlow);
    document.querySelectorAll('.js-sync').forEach(sync);
  });
})();
