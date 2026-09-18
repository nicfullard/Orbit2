// Gantt drag-to-reschedule (spec §6.16). On a row the viewer may edit: drag a bar to move both dates, drag either end
// to change the start or the due date alone (an end dragged on a start-only bar gives it a due date), or drag a
// milestone to move its due date. Moves snap to whole days and a label shows the dates while dragging; releasing posts
// the hidden form and the page reloads with the saved plan. Only the dragged task moves - successors never shift.
(function () {
  var canvas = document.getElementById('ganttCanvas');
  var form = document.getElementById('ganttReschedule');
  if (!canvas || !form) return;
  var px = parseInt(canvas.dataset.pxPerDay, 10) || 24;
  var label = document.getElementById('ganttDragLabel');
  var EDGE = 7;
  var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  var drag = null;

  function parse(iso) { return iso ? new Date(iso + 'T00:00:00Z') : null; }
  function toIso(d) { return d ? d.toISOString().slice(0, 10) : ''; }
  function addDays(iso, days) { var d = parse(iso); if (!d) return ''; d.setUTCDate(d.getUTCDate() + days); return toIso(d); }
  function pretty(iso) { var d = parse(iso); return d ? d.getUTCDate() + ' ' + MONTHS[d.getUTCMonth()] : '?'; }
  function spanDays(start, end) { var a = parse(start), b = parse(end); return a && b ? Math.round((b - a) / 86400000) : 0; }

  // Where on the mark the pointer is: an end (resize) or the body (move). Milestones only move.
  function modeAt(el, clientX) {
    if (el.classList.contains('gantt-milestone')) return 'move';
    var r = el.getBoundingClientRect();
    if (r.width > 3 * EDGE) {
      if (clientX - r.left <= EDGE) return 'start';
      if (r.right - clientX <= EDGE) return 'end';
    }
    return 'move';
  }

  function tentative(d, days) {
    var s = d.start, e = d.end;
    if (d.mode === 'move') { s = addDays(s, days); e = addDays(e, days); }
    else if (d.mode === 'start') { s = addDays(s, days); }
    else { e = addDays(e || d.start, days); }
    return { start: s, end: e };
  }

  // The start can't pass the due date and vice versa.
  function clamp(d, days) {
    var span = spanDays(d.start, d.end);
    if (d.mode === 'start' && d.end) return Math.min(days, span);
    if (d.mode === 'end') return Math.max(days, d.end ? -span : 0);
    return days;
  }

  function render(d, days) {
    var el = d.el;
    if (d.mode === 'move') { el.style.left = (d.left + days * px) + 'px'; }
    else if (d.mode === 'start') { el.style.left = (d.left + days * px) + 'px'; el.style.width = Math.max(4, d.width - days * px) + 'px'; }
    else { el.style.width = Math.max(4, d.width + days * px) + 'px'; }
    if (!label) return;
    var t = tentative(d, days);
    label.textContent = t.start && t.end ? pretty(t.start) + ' → ' + pretty(t.end) : t.start ? 'starts ' + pretty(t.start) : 'due ' + pretty(t.end);
    label.style.left = el.offsetLeft + 'px';
    label.style.top = (el.offsetTop - 22) + 'px';
    label.hidden = false;
  }

  canvas.addEventListener('pointermove', function (e) {
    if (drag) {
      drag.days = clamp(drag, Math.round((e.clientX - drag.x0) / px));
      render(drag, drag.days);
      return;
    }
    var el = e.target.closest('[data-editable="true"]');
    if (el) el.style.cursor = modeAt(el, e.clientX) === 'move' ? 'grab' : 'ew-resize';
  });

  canvas.addEventListener('pointerdown', function (e) {
    var el = e.target.closest('[data-editable="true"]');
    if (!el || e.button !== 0) return;
    e.preventDefault();
    drag = {
      el: el, mode: modeAt(el, e.clientX), x0: e.clientX, left: el.offsetLeft, width: el.offsetWidth,
      start: el.dataset.start || '', end: el.dataset.end || '', days: 0
    };
    el.classList.add('gantt-dragging');
    try { el.setPointerCapture(e.pointerId); } catch (err) { /* pointer capture is optional */ }
  });

  function finish() {
    if (!drag) return;
    var d = drag;
    drag = null;
    d.el.classList.remove('gantt-dragging');
    if (label) label.hidden = true;
    if (d.days === 0) {
      d.el.style.left = d.left + 'px';
      d.el.style.width = d.width + 'px';
      return;
    }
    var t = tentative(d, d.days);
    form.querySelector('[name=taskId]').value = d.el.dataset.task;
    form.querySelector('[name=startDate]').value = t.start;
    form.querySelector('[name=dueDate]').value = t.end;
    form.submit();
  }
  canvas.addEventListener('pointerup', finish);
  canvas.addEventListener('pointercancel', finish);
})();
