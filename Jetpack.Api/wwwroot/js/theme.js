// Applies the saved or preferred colour theme before the page renders, so
// there is no flash of the wrong theme. Loaded synchronously in <head>.
(() => {
  let theme = null;
  try {
    theme = localStorage.getItem("jetpack-theme");
  } catch {
    // Storage may be unavailable (e.g. private mode).
  }
  if (theme !== "light" && theme !== "dark") {
    theme = window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  document.documentElement.setAttribute("data-bs-theme", theme);
})();
