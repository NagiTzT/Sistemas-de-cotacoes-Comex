// Keep the desktop sidebar preference across Blazor enhanced navigation.
(() => {
    const key = "fullbrands.quotations.sidebar";
    function restore() {
        const checkbox = document.getElementById("sidebar-checkbox");
        if (!checkbox) return;
        try { checkbox.checked = innerWidth > 700 && localStorage.getItem(key) === "open"; }
        catch { /* Navigation remains usable when storage is unavailable. */ }
    }
    document.addEventListener("change", event => {
        if (event.target.id !== "sidebar-checkbox" || innerWidth <= 700) return;
        try { localStorage.setItem(key, event.target.checked ? "open" : "closed"); } catch { }
    });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            const checkbox = document.getElementById("sidebar-checkbox");
            if (checkbox) { checkbox.checked = false; checkbox.dispatchEvent(new Event("change", { bubbles: true })); }
        }
    });
    Blazor.addEventListener("enhancedload", restore);
    restore();
})();
