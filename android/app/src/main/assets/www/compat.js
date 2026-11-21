// Compatibility shims for old Android System WebViews.
//
// The panel needs a few DOM and ES APIs that shipped after Chromium 83, which is what older phones such as a Redmi
// Note 7 are stuck on (the system WebView cannot always be updated there). Without these the module throws on the
// first call and the whole panel stays black. Loaded as a classic script before i18n.js and main.js.
(function () {
  // Element.replaceChildren — Chromium 86. Used all over the panel's render paths.
  var proto = window.Element && window.Element.prototype;
  if (proto && typeof proto.replaceChildren !== 'function') {
    var replaceChildren = function () {
      while (this.firstChild) this.removeChild(this.firstChild);
      if (arguments.length) this.append.apply(this, arguments);
    };
    [window.Element, window.Document, window.DocumentFragment].forEach(function (ctor) {
      if (ctor && ctor.prototype && typeof ctor.prototype.replaceChildren !== 'function') {
        ctor.prototype.replaceChildren = replaceChildren;
      }
    });
  }

  // Array.prototype.at — Chromium 92.
  if (!Array.prototype.at) {
    Array.prototype.at = function (index) {
      var length = this.length >>> 0;
      var k = Math.trunc(index) || 0;
      if (k < 0) k += length;
      return k < 0 || k >= length ? undefined : this[k];
    };
  }

  // String.prototype.replaceAll — Chromium 85.
  if (!String.prototype.replaceAll) {
    String.prototype.replaceAll = function (search, replacement) {
      if (search instanceof RegExp) {
        if (!search.global) throw new TypeError('replaceAll must be called with a global RegExp');
        return this.replace(search, replacement);
      }
      return this.split(String(search)).join(replacement);
    };
  }
})();
