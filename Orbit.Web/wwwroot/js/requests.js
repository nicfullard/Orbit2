// Request flows (spec §6.20). On the flow page the questions are shown one at a time: Next checks the step before moving on,
// Back returns, and a last step reviews every answer (each with a Change link back to its step) before the request is logged.
// Without this script the page shows every question at once with one button, and the server checks the answers either way.
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
    if (checked && !checked.dataset.describe) {
      var label = step.querySelector('label[for="' + checked.id + '"]');
      parts.push(checked.classList.contains('js-asset-choice')
        ? textOf(label.querySelector('.js-choice-label')) + ' (linked to the request)'
        : [textOf(label.querySelector('.js-choice-label')), textOf(label.querySelector('.js-choice-detail'))].filter(Boolean).join(' - '));
    }
    var select = step.querySelector('select');
    if (select && select.value) parts.push(select.options[select.selectedIndex].text);
    step.querySelectorAll('textarea, input[type="text"], input[type="number"], input[type="date"]').forEach(function (f) {
      if (!f.disabled && f.value.trim()) parts.push(f.value.trim());
    });
    return parts.join('\n');
  }

  // An Asset question lists the person's assets and "Something else": the description box shows (and posts) only for the last.
  // A question without the list - the person holds no assets - is just the box.
  function syncDescribe(step, focus) {
    var box = step.querySelector('.js-asset-describe');
    if (!box) return;
    var chosen = step.querySelector('.js-asset-choice:checked');
    var show = !!(chosen && chosen.dataset.describe);
    box.hidden = !show;
    box.querySelectorAll('input').forEach(function (i) { i.disabled = !show; });
    if (show && focus) box.querySelector('input').focus();
  }

  function setupFlow(form) {
    var steps = Array.prototype.slice.call(form.querySelectorAll('.js-step'));
    var review = form.querySelector('.js-review-step');
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
      if (step.dataset.assetRequired) {
        var text = step.querySelector('.js-asset-text');
        var described = text && !text.disabled && text.value.trim();
        var chosen = step.querySelector('.js-asset-choice:checked');
        if (step.querySelector('.js-asset-choice') && !chosen) message = 'Choose one of your assets, or Something else.';
        else if ((!chosen || chosen.dataset.describe) && !described) message = 'Describe it: its name, asset number or where it is.';
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
      back.hidden = index === 0;
      next.hidden = onReview;
      submit.hidden = !onReview;
      counter.textContent = onReview ? 'Check and log your request' : 'Step ' + (index + 1) + ' of ' + (pages.length - 1);
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

    // Choosing an asset or "Something else" shows or hides the description box - before the step is checked again below.
    steps.forEach(function (s) { syncDescribe(s, false); });
    form.addEventListener('change', function (e) {
      if (e.target.classList.contains('js-asset-choice')) syncDescribe(e.target.closest('.js-step'), true);
    });

    // Once a step shows why it can't be left, answering it clears the message (and a still-wrong answer says so again).
    function recheck(e) {
      var step = e.target.closest && e.target.closest('.js-step');
      var error = step && step.querySelector('.js-step-error');
      if (error && error.textContent) check(step);
    }
    form.addEventListener('input', recheck);
    form.addEventListener('change', recheck);

    // Enter moves on from a one-line answer, as it would submit a one-field form - not from a text area.
    form.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter' || e.defaultPrevented) return;
      var t = e.target;
      if (t.tagName === 'TEXTAREA' || t.tagName === 'BUTTON' || t.type === 'file') return;
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
      submit.textContent = 'Logging...';
    });

    // After a refused post, open the first question the server marked; otherwise start at the beginning.
    var marked = steps.findIndex(function (s) { return s.dataset.hasError; });
    show(marked >= 0 ? marked : 0, marked >= 0);
  }

  // Configuring a flow: a question's type decides whether its choices and its "sets the due date" box apply, and an option's kind
  // whether it has a web address or a task type and files step. Hidden fields still post, and the server ignores what doesn't apply.
  function syncConfig(select) {
    var form = select.form;
    if (!form) return;
    var attribute = select.classList.contains('js-question-type') ? 'forType' : 'forKind';
    form.querySelectorAll(attribute === 'forType' ? '[data-for-type]' : '[data-for-kind]').forEach(function (el) {
      el.hidden = el.dataset[attribute].split(' ').indexOf(select.value) < 0;
    });
  }

  document.addEventListener('change', function (e) {
    if (e.target.matches && e.target.matches('.js-question-type, .js-option-kind')) syncConfig(e.target);
  });

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.js-request-flow').forEach(setupFlow);
    document.querySelectorAll('.js-question-type, .js-option-kind').forEach(syncConfig);
  });
})();
