(() => {
  "use strict";

  const config = window.SqlMoverSiteConfig ?? {};
  const downloadPath = typeof config.downloadPath === "string"
    ? config.downloadPath.trim()
    : "";

  // パス未設定時は何も公開しない。HTML側も初期状態をhiddenにしている。
  if (!downloadPath) return;

  document.querySelectorAll("[data-download-link]").forEach((link) => {
    link.setAttribute("href", downloadPath);
  });

  document.querySelectorAll("[data-download]").forEach((element) => {
    element.hidden = false;
  });

  const version = typeof config.version === "string" ? config.version.trim() : "";
  if (!version) return;

  document.querySelectorAll("[data-version]").forEach((element) => {
    element.textContent = `バージョン ${version}`;
    element.hidden = false;
  });
})();
