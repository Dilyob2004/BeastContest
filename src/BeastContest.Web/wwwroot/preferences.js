window.beast = {
    preferences() {
        try { return [localStorage.getItem("beast.language") || "ru", localStorage.getItem("beast.theme") || "dark"]; }
        catch { return ["ru", "dark"]; }
    },
    apply(language, theme) {
        document.documentElement.lang = language; document.cookie = "beast.language=" + language + ";path=/;SameSite=Lax";
        document.documentElement.dataset.theme = theme;
        try { localStorage.setItem("beast.language", language); localStorage.setItem("beast.theme", theme); } catch {}
    },
    readDraft(key) { try { return localStorage.getItem("beast.draft." + key); } catch { return null; } },
    saveDraft(key, code) { try { localStorage.setItem("beast.draft." + key, code); } catch {} },
    downloadText(filename, text) {
        const url = URL.createObjectURL(new Blob(["\uFEFF", text], { type: "text/plain;charset=utf-8" }));
        const link = document.createElement("a"); link.href = url; link.download = filename;
        document.body.appendChild(link); link.click(); link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
};
window.beast.apply(...window.beast.preferences());

