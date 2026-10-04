using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Lokalna baza hasel (szyfrowana DPAPI per user) z importem/eksportem CSV.
    public partial class MainWindow
    {
        sealed class SavedPasswordEntry
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public string Host { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string Notes { get; set; }
            public string Source { get; set; }
            public long UpdatedUnix { get; set; }
        }

        sealed class PasswordVaultData
        {
            public int Version { get; set; }
            public List<SavedPasswordEntry> Entries { get; set; }
        }

        static string PasswordVaultFile { get { return Path.Combine(DataDir, "hasla.vault"); } }
        static readonly byte[] PasswordVaultEntropy = Encoding.UTF8.GetBytes("Velivo.Passwords.v1");
        List<SavedPasswordEntry> _passwordEntries;
        readonly Dictionary<string, DateTime> _passwordPrompted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        string _pendingFillHost;
        string _pendingFillUser;
        string _pendingFillPass;
        DateTime _pendingFillUntilUtc;
        readonly HashSet<CoreWebView2> _passwordVaultScriptsRegistered = new HashSet<CoreWebView2>();
        readonly HashSet<CoreWebView2> _passwordVaultHooks = new HashSet<CoreWebView2>();
        readonly Dictionary<CoreWebView2, DispatcherTimer> _passwordCaptureTimers = new Dictionary<CoreWebView2, DispatcherTimer>();
        readonly HashSet<CoreWebView2> _passwordCaptureInProgress = new HashSet<CoreWebView2>();

        static readonly string PasswordVaultDocumentCreatedScript = """
            (() => {
                try {
                    if (window.__velivoPasswordHelper && window.__velivoPasswordHelper.version === 4) {
                        const helper = window.__velivoPasswordHelper;
                        return JSON.stringify(typeof helper.initialize === 'function' ? helper.initialize() : helper.status());
                    }

                    const state = { credentials: [], panel: null, boundPanel: null, active: null, activeLogin: null, activePassword: null, observer: null, refreshQueued: false, pendingCandidate: null, pendingFill: null };
                    const isInput = (el) => !!el && (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement);
                    const isVisible = (el) => {
                        if (!el || !el.isConnected || el.disabled || el.type === 'hidden') return false;
                        const r = el.getBoundingClientRect();
                        return !!r && r.width > 1 && r.height > 1 && getComputedStyle(el).visibility !== 'hidden' && getComputedStyle(el).display !== 'none';
                    };
                    const allInputs = (root = document) => {
                        try { return Array.from(root.querySelectorAll('input:not([type=hidden]), textarea')).filter(isVisible); }
                        catch (_) { return []; }
                    };
                    // pola wyszukiwania (Startpage, Google, sklepy) nie sa polami logowania
                    const isSearchField = (el) => {
                        try {
                            return el.type === 'search' || (el.getAttribute('role') || '') === 'searchbox' || (el.getAttribute('role') || '') === 'combobox' ||
                                /^(q|query|search|s|k|keyword|szukaj)$/i.test(el.name || '') || /search|szukaj|wyszuk/i.test(`${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''}`) ||
                                !!el.closest('form[role=search], [role=search]');
                        } catch (_) { return false; }
                    };
                    const isLoginField = (el) => !!el && isInput(el) && !(el instanceof HTMLTextAreaElement) && !isSearchField(el) &&
                        (el.type === 'email' || /username|email|e-mail|login|user|konto|uzytkownik/i.test(`${el.name || ''} ${el.id || ''} ${el.autocomplete || ''}`) || el.type === 'text');
                    const isPasswordField = (el) => !!el && isInput(el) && (el.type === 'password' || /current-password|new-password/i.test(el.autocomplete || ''));
                    const visiblePassword = (root) => allInputs(root).find(isPasswordField) || null;
                    const visibleLogin = (root, exclude) => allInputs(root).find(el => el !== exclude && isLoginField(el)) || null;

                    function setValue(el, value) {
                        if (!el || !isInput(el)) return false;
                        try {
                            const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                            const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                            if (setter) setter.call(el, value); else el.value = value;
                            el.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
                            el.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
                            return true;
                        } catch (_) { return false; }
                    }

                    function submitLoginForm(login, password, username, passwordValue) {
                        if (!login || !password || !login.form || login.form !== password.form) return;
                        const form = password.form;
                        setTimeout(() => {
                            try {
                                if (!form.isConnected || !isVisible(login) || !isVisible(password) ||
                                    login.form !== form || password.form !== form || !login.value || !password.value ||
                                    (username && login.value !== username) || password.value !== passwordValue) return;
                                const buttons = Array.from(form.querySelectorAll('button,input[type=submit],input[type=button],[role=button]'))
                                    .filter(button => isVisible(button) && !button.disabled);
                                const submitter = buttons.find(button => button.matches('button[type=submit],input[type=submit]')) ||
                                    buttons.find(button => /zaloguj|sign\s*in|log\s*in|dalej|kontynuuj|continue/i.test(button.innerText || button.value || ''));
                                if (submitter) submitter.click();
                                else if (typeof form.requestSubmit === 'function') form.requestSubmit();
                            } catch (_) {}
                        }, 150);
                    }

                    function setCandidate(login, password, source) {
                        if (!password || String(password).length < 1) return;
                        state.pendingCandidate = { username: login || '', password: password, url: location.href, source: source || 'page' };
                    }

                    function ensurePanel() {
                        try {
                            if (!document.documentElement) return false;
                            if (!document.getElementById('__velivoPwdIconsStyle')) {
                                const style = document.createElement('style');
                                style.id = '__velivoPwdIconsStyle';
                                style.textContent = '.velivo-pwd-panel{position:fixed!important;z-index:2147483647!important;display:none;gap:6px;align-items:center;background:rgba(16,24,40,.96);border:1px solid #64748b;border-radius:9px;padding:4px;box-shadow:0 6px 22px rgba(0,0,0,.32);font:13px sans-serif;line-height:1}.velivo-pwd-panel button{border:1px solid #94a3b8;background:#f8fafc;color:#0f172a;border-radius:6px;padding:4px 8px;cursor:pointer;font:inherit}.velivo-pwd-panel button:disabled{opacity:.45;cursor:default}.velivo-pwd-choices{display:none;position:absolute;z-index:2147483647;width:280px;max-width:calc(100vw - 12px);max-height:220px;overflow-x:hidden;overflow-y:auto;overscroll-behavior-y:contain;touch-action:pan-y;pointer-events:auto;background:#fff;color:#111827;border:1px solid #94a3b8;border-radius:7px;box-shadow:0 8px 24px rgba(0,0,0,.35);padding:3px}.velivo-pwd-choice{display:block;width:100%;text-align:left;white-space:normal;border:0!important;border-radius:4px!important;background:#fff!important;padding:7px 9px!important;color:#111827!important}.velivo-pwd-choice:hover,.velivo-pwd-choice:focus{background:#e8f0fe!important}.velivo-pwd-choice-name{display:block;font-weight:600}.velivo-pwd-choice-user{display:block;margin-top:3px;color:#475569;font-size:11px}';
                                document.documentElement.appendChild(style);
                            }
                            let panel = document.getElementById('__velivoPwdPanel');
                            if (!panel) {
                                panel = document.createElement('div');
                                panel.id = '__velivoPwdPanel';
                                panel.className = 'velivo-pwd-panel';
                                panel.innerHTML = '<button type=button data-v=fill title="Wybierz wpis z lokalnej bazy">&#128273;</button><button type=button data-v=gen title="Wygeneruj haslo">&#9889;</button><div class=velivo-pwd-choices role=listbox style="display:none"></div>';
                                (document.body || document.documentElement).appendChild(panel);
                            }
                            state.panel = panel;
                            return true;
                        } catch (_) { return false; }
                    }

                    function locateFields(anchor) {
                        let root = null;
                        try { root = anchor && anchor.form ? anchor.form : document; } catch (_) { root = document; }
                        let password = isPasswordField(anchor) ? anchor : visiblePassword(root);
                        let login = isLoginField(anchor) ? anchor : visibleLogin(root, password);
                        if (!login && password) login = visibleLogin(password.form || document, password);
                        // pole tekstowe bez hasla w poblizu to nie formularz logowania - chyba ze wprost wyglada na login
                        if (!password && login && !(login.type === 'email' || /username|email|e-mail|login|user|uzytkownik/i.test(`${login.name || ''} ${login.id || ''} ${login.autocomplete || ''}`))) login = null;
                        return { login: login, password: password };
                    }

                    function bindPanelClick() {
                        if (state.panel && state.panelClick && state.boundPanel !== state.panel) {
                            state.panel.addEventListener('click', state.panelClick);
                            state.boundPanel = state.panel;
                        }
                    }

                    function positionPanel(anchor) {
                        try {
                            if (!state.panel || !anchor || !isVisible(anchor)) { if (state.panel) state.panel.style.display = 'none'; return; }
                            const rect = anchor.getBoundingClientRect();
                            state.panel.style.display = 'inline-flex';
                            const width = state.panel.getBoundingClientRect().width || 96;
                            const height = state.panel.getBoundingClientRect().height || 30;
                            // obok pola, po prawej stronie (nie zaslania pol ani przyciskow);
                            // gdy po prawej brak miejsca - pod polem, przy jego prawej krawedzi
                            let left, top;
                            if (rect.right + 6 + width <= window.innerWidth - 6) {
                                left = rect.right + 6;
                                top = rect.top + (rect.height - height) / 2;
                            } else {
                                left = rect.right - width;
                                top = rect.bottom + 4;
                            }
                            left = Math.max(6, Math.min(window.innerWidth - width - 6, left));
                            top = Math.max(6, Math.min(window.innerHeight - height - 4, top));
                            state.panel.style.left = `${left}px`;
                            state.panel.style.top = `${top}px`;
                            const choices = state.panel.querySelector('.velivo-pwd-choices');
                            if (choices && choices.style.display !== 'none') {
                                const choicesWidth = Math.min(280, window.innerWidth - 12);
                                const choicesLeft = Math.max(6, Math.min(window.innerWidth - choicesWidth - 6, rect.left));
                                choices.style.left = `${choicesLeft - left}px`;
                                if (window.innerHeight - rect.bottom < 245) {
                                    choices.style.top = 'auto';
                                    choices.style.bottom = 'calc(100% + 4px)';
                                } else {
                                    choices.style.bottom = 'auto';
                                    choices.style.top = 'calc(100% + 4px)';
                                }
                            }
                        } catch (_) { if (state.panel) state.panel.style.display = 'none'; }
                    }

                    function updateFrameCredentials(frame) {
                        try {
                            const child = frame && frame.contentWindow;
                            if (!child || child.location.origin !== location.origin) return;
                            if (child.__velivoPasswordHelper) child.__velivoPasswordHelper.setCredentials(state.credentials, false);
                        } catch (_) {}
                    }

                    function refresh() {
                        state.refreshQueued = false;
                        if (!ensurePanel()) return;
                        bindPanelClick();
                        if (state.pendingFill && state.pendingFill.expires > Date.now() && state.pendingFill.host === location.host.toLowerCase()) {
                            const pendingPassword = visiblePassword(document);
                            if (pendingPassword) {
                                const pendingLogin = visibleLogin(pendingPassword.form || document, pendingPassword);
                                if (pendingLogin && state.pendingFill.username) setValue(pendingLogin, state.pendingFill.username);
                                setValue(pendingPassword, state.pendingFill.password);
                                submitLoginForm(pendingLogin, pendingPassword, state.pendingFill.username, state.pendingFill.password);
                                state.pendingFill = null;
                            }
                        }
                        const active = document.activeElement;
                        if (state.panel.contains(active)) return;
                        const choices = state.panel.querySelector('.velivo-pwd-choices');
                        if (choices && choices.style.display !== 'none' &&
                            (!isInput(active) || active === state.active || active === state.activeLogin || active === state.activePassword)) {
                            positionPanel(state.activePassword || state.activeLogin);
                            return;
                        }
                        if (choices) choices.style.display = 'none';
                        if (!isInput(active)) {
                            state.active = null;
                            state.activeLogin = null;
                            state.activePassword = null;
                            state.panel.style.display = 'none';
                            return;
                        }
                        const fields = locateFields(active);
                        if (fields.password && state.pendingFill && state.pendingFill.expires > Date.now() && state.pendingFill.host === location.host.toLowerCase()) {
                            if (fields.login && state.pendingFill.username) setValue(fields.login, state.pendingFill.username);
                            setValue(fields.password, state.pendingFill.password);
                            submitLoginForm(fields.login, fields.password, state.pendingFill.username, state.pendingFill.password);
                            state.pendingFill = null;
                        }
                        state.active = active;
                        state.activeLogin = fields.login;
                        state.activePassword = fields.password;
                        const fill = state.panel.querySelector('[data-v=fill]');
                        const generate = state.panel.querySelector('[data-v=gen]');
                        if (fill) fill.disabled = state.credentials.length === 0 || (!fields.login && !fields.password);
                        if (generate) generate.disabled = !fields.password;
                        if (generate) generate.style.display = fields.password ? '' : 'none';
                        // bez pola hasla panel ma sens tylko, gdy sa zapisane konta do wypelnienia
                        if (!fields.password && (!fields.login || state.credentials.length === 0)) { state.panel.style.display = 'none'; return; }
                        positionPanel(fields.password || fields.login);
                        try {
                            document.querySelectorAll('iframe').forEach(updateFrameCredentials);
                        } catch (_) {}
                    }

                    function scheduleRefresh() {
                        if (state.refreshQueued) return;
                        state.refreshQueued = true;
                        try { requestAnimationFrame(refresh); } catch (_) { setTimeout(refresh, 0); }
                    }

                    function showCredentialChoices() {
                        const choices = state.panel && state.panel.querySelector('.velivo-pwd-choices');
                        if (!choices) return;
                        choices.replaceChildren();
                        const login = state.activeLogin;
                        const current = login && login.value ? login.value.trim().toLowerCase() : '';
                        const ordered = state.credentials.map((entry, index) => ({ entry: entry, index: index }))
                            .sort((a, b) => Number((b.entry.u || '').trim().toLowerCase() === current) - Number((a.entry.u || '').trim().toLowerCase() === current));
                        for (const item of ordered) {
                            const button = document.createElement('button');
                            button.type = 'button';
                            button.className = 'velivo-pwd-choice';
                            button.dataset.v = 'choose';
                            button.dataset.index = String(item.index);
                            const name = document.createElement('span');
                            name.className = 'velivo-pwd-choice-name';
                            name.textContent = item.entry.n || item.entry.u || 'Zapisane konto';
                            const user = document.createElement('span');
                            user.className = 'velivo-pwd-choice-user';
                            user.textContent = item.entry.u || item.entry.url || '';
                            button.append(name, user);
                            choices.appendChild(button);
                        }
                        choices.style.display = 'block';
                        positionPanel(state.activePassword || state.activeLogin);
                    }

                    function takeCandidate() {
                        try {
                            if (state.pendingCandidate) {
                                const candidate = state.pendingCandidate;
                                state.pendingCandidate = null;
                                return candidate;
                            }
                            const frames = document.querySelectorAll('iframe');
                            for (const frame of frames) {
                                try {
                                    if (frame.contentWindow.location.origin !== location.origin) continue;
                                    const helper = frame.contentWindow.__velivoPasswordHelper;
                                    const candidate = helper && helper.takeCandidate();
                                    if (candidate) return candidate;
                                } catch (_) {}
                            }
                        } catch (_) {}
                        return null;
                    }

                    function setCredentials(credentials, updateFrames = true) {
                        state.credentials = Array.isArray(credentials) ? credentials.filter(x => x && typeof x.p === 'string') : [];
                        scheduleRefresh();
                        if (updateFrames) {
                            try { document.querySelectorAll('iframe').forEach(updateFrameCredentials); } catch (_) {}
                        }
                        return status();
                    }

                    function status() {
                        let inputsSeen = 0;
                        let activeCandidateDetected = false;
                        try {
                            inputsSeen = allInputs().length;
                            activeCandidateDetected = !!locateFields(document.activeElement).login || !!locateFields(document.activeElement).password;
                        } catch (_) {}
                        return { panelCreated: !!(state.panel && state.panel.isConnected), inputsSeen: inputsSeen, activeCandidateDetected: activeCandidateDetected };
                    }

                    function bindEvents() {
                        if (state.eventsBound) return;
                        state.eventsBound = true;
                        document.addEventListener('focusin', event => {
                            if (state.panel && state.panel.contains(event.target)) return;
                            if (isInput(event.target)) scheduleRefresh();
                            else if (state.panel) {
                                const choices = state.panel.querySelector('.velivo-pwd-choices');
                                if (!choices || choices.style.display === 'none') state.panel.style.display = 'none';
                            }
                        }, true);
                        document.addEventListener('click', event => {
                            if (state.panel && state.panel.contains(event.target)) return;
                            if (isInput(event.target)) { state.active = event.target; scheduleRefresh(); }
                        }, true);
                        document.addEventListener('input', event => { if (isInput(event.target)) scheduleRefresh(); }, true);
                        window.addEventListener('wheel', event => {
                            const choices = state.panel && state.panel.querySelector('.velivo-pwd-choices');
                            if (!choices || choices.style.display === 'none' || !event.composedPath().includes(choices)) return;
                            const delta = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? event.deltaY * 16 :
                                event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? event.deltaY * choices.clientHeight : event.deltaY;
                            if (choices.scrollHeight > choices.clientHeight) {
                                event.preventDefault();
                                choices.scrollTop += delta;
                            }
                            event.stopImmediatePropagation();
                        }, { capture: true, passive: false });
                        document.addEventListener('submit', event => {
                            try {
                                const form = event.target;
                                const password = visiblePassword(form);
                                const login = visibleLogin(form, password);
                                if (password && password.value) setCandidate(login && login.value, password.value, 'submit');
                            } catch (_) {}
                        }, true);
                        document.addEventListener('load', event => {
                            if (event.target && event.target.tagName === 'IFRAME') { updateFrameCredentials(event.target); scheduleRefresh(); }
                        }, true);
                        window.addEventListener('scroll', event => {
                            const choices = state.panel && state.panel.querySelector('.velivo-pwd-choices');
                            if (choices && choices.style.display !== 'none' &&
                                (event.target === choices || choices.contains(event.target))) return;
                            positionPanel(state.activePassword || state.activeLogin);
                        }, true);
                        window.addEventListener('resize', () => positionPanel(state.activePassword || state.activeLogin), true);
                        const observe = () => {
                            try {
                                if (!state.observer && document.documentElement) {
                                    state.observer = new MutationObserver(() => scheduleRefresh());
                                    state.observer.observe(document.documentElement, {
                                        childList: true,
                                        subtree: true,
                                        attributes: true,
                                        attributeFilter: ['type', 'autocomplete', 'name', 'id', 'class', 'style', 'hidden']
                                    });
                                }
                            } catch (_) {}
                        };
                        observe();
                        document.addEventListener('DOMContentLoaded', observe, { once: true });
                    }

                    state.panelClick = event => {
                        try {
                            const button = event.target && event.target.closest ? event.target.closest('button[data-v]') : null;
                            if (!button || !state.panel || !state.panel.contains(button)) return;
                            const fields = locateFields(state.active);
                            const login = fields.login || state.activeLogin;
                            const password = fields.password || state.activePassword;
                            if (button.dataset.v === 'fill') {
                                if (!state.credentials.length) return;
                                showCredentialChoices();
                                return;
                            }
                            if (button.dataset.v === 'choose') {
                                const index = Number(button.dataset.index);
                                const entry = Number.isInteger(index) ? state.credentials[index] : null;
                                if (!entry) return;
                                state.pendingFill = { username: entry.u || '', password: entry.p || '', host: location.host.toLowerCase(), expires: Date.now() + 120000 };
                                if (login && entry.u) setValue(login, entry.u);
                                if (password && entry.p) setValue(password, entry.p);
                                if (entry.p) setCandidate(entry.u || (login && login.value), entry.p, 'fill');
                                const choices = state.panel.querySelector('.velivo-pwd-choices');
                                if (choices) choices.style.display = 'none';
                                scheduleRefresh();
                            } else if (button.dataset.v === 'gen' && password) {
                                const alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%^&*()-_=+[]{};:,.?';
                                const bytes = new Uint32Array(16);
                                try { crypto.getRandomValues(bytes); } catch (_) { for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 0xffffffff); }
                                let value = '';
                                for (const byte of bytes) value += alphabet[byte % alphabet.length];
                                setValue(password, value);
                                setCandidate(login && login.value, value, 'generator');
                                scheduleRefresh();
                            }
                        } catch (_) {}
                    };

                    function initialize() {
                        ensurePanel();
                        bindEvents();
                        bindPanelClick();
                        scheduleRefresh();
                        return status();
                    }

                    window.__velivoPasswordHelper = {
                        version: 4,
                        initialize: initialize,
                        setCredentials: setCredentials,
                        takeCandidate: takeCandidate,
                        status: status
                    };
                    return JSON.stringify(initialize());
                } catch (_) {
                    return JSON.stringify({ panelCreated: false, inputsSeen: 0, activeCandidateDetected: false });
                }
            })();
            """;

        void EnsurePasswordVaultLoaded()
        {
            if (_passwordEntries != null) return;
            _passwordEntries = new List<SavedPasswordEntry>();
            try
            {
                if (!File.Exists(PasswordVaultFile)) return;
                var raw = File.ReadAllText(PasswordVaultFile);
                if (string.IsNullOrWhiteSpace(raw)) return;
                var enc = Convert.FromBase64String(raw);
                var plain = ProtectedData.Unprotect(enc, PasswordVaultEntropy, DataProtectionScope.CurrentUser);
                var vault = JsonSerializer.Deserialize<PasswordVaultData>(plain);
                if (vault != null && vault.Entries != null)
                    _passwordEntries = vault.Entries.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Password)).ToList();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void SavePasswordVault()
        {
            try
            {
                EnsurePasswordVaultLoaded();
                Directory.CreateDirectory(DataDir);
                var data = new PasswordVaultData
                {
                    Version = 1,
                    Entries = _passwordEntries.OrderBy(e => e.Host, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Username, StringComparer.OrdinalIgnoreCase).ToList()
                };
                var plain = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true });
                var enc = ProtectedData.Protect(plain, PasswordVaultEntropy, DataProtectionScope.CurrentUser);
                File.WriteAllText(PasswordVaultFile, Convert.ToBase64String(enc));
                NotifyLanStateChanged();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        sealed class PasswordSyncEntry
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public string Host { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string Notes { get; set; }
            public string Source { get; set; }
            public long UpdatedUnix { get; set; }
        }

        string ExportPasswordsForSync()
        {
            EnsurePasswordVaultLoaded();
            var list = _passwordEntries
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Password))
                .OrderBy(x => x.Host ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Username ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(x => new PasswordSyncEntry
                {
                    Name = x.Name,
                    Url = x.Url,
                    Host = x.Host,
                    Username = x.Username,
                    Password = x.Password,
                    Notes = x.Notes,
                    Source = x.Source,
                    UpdatedUnix = x.UpdatedUnix
                })
                .ToList();
            return JsonSerializer.Serialize(list);
        }

        static string PasswordSyncIdentity(SavedPasswordEntry entry)
        {
            var host = (entry.Host ?? "").Trim();
            if (host.Length == 0) host = HostFromAnyUrl(entry.Url);
            host = host.TrimEnd('.').ToLowerInvariant();
            var username = (entry.Username ?? "").Trim().ToLowerInvariant();
            if (host.Length > 0 && username.Length > 0) return host + "\n" + username;
            return host + "\n" + (entry.Url ?? "").Trim().TrimEnd('/').ToLowerInvariant() + "\n" + (entry.Name ?? "").Trim().ToLowerInvariant() + "\n" + username;
        }

        void ImportPasswordsFromSync(string json, bool merge = false)
        {
            if (json == null) return;
            try
            {
                var src = JsonSerializer.Deserialize<List<PasswordSyncEntry>>(json) ?? new List<PasswordSyncEntry>();
                var incoming = src
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Password))
                    .Select(x => new SavedPasswordEntry
                    {
                        Name = x.Name,
                        Url = x.Url,
                        Host = x.Host,
                        Username = x.Username,
                        Password = x.Password,
                        Notes = x.Notes,
                        Source = x.Source,
                        UpdatedUnix = x.UpdatedUnix
                    })
                    .ToList();
                if (merge)
                {
                    EnsurePasswordVaultLoaded();
                    var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < _passwordEntries.Count; i++)
                    {
                        var identity = PasswordSyncIdentity(_passwordEntries[i]);
                        if (!indexes.ContainsKey(identity)) indexes[identity] = i;
                    }
                    foreach (var entry in incoming)
                    {
                        var identity = PasswordSyncIdentity(entry);
                        int index;
                        if (indexes.TryGetValue(identity, out index))
                        {
                            if (entry.UpdatedUnix > _passwordEntries[index].UpdatedUnix) _passwordEntries[index] = entry;
                        }
                        else
                        {
                            indexes[identity] = _passwordEntries.Count;
                            _passwordEntries.Add(entry);
                        }
                    }
                }
                else _passwordEntries = incoming;
                SavePasswordVault();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static string HostFromAnyUrl(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return "";
                Uri u;
                if (Uri.TryCreate(url, UriKind.Absolute, out u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && IsUsableWebHost(u.Host))
                    return (u.Host ?? "").ToLowerInvariant();
            }
            catch (Exception) { }
            return "";
        }

        static bool IsUsableWebHost(string host)
        {
            host = (host ?? "").Trim().Trim('[', ']').ToLowerInvariant();
            return host == "localhost" || host.Contains('.') || Uri.CheckHostName(host) == UriHostNameType.IPv6;
        }

        static string CsvEscape(string s)
        {
            var v = s ?? "";
            if (v.Contains("\"") || v.Contains(",") || v.Contains("\n") || v.Contains("\r"))
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        static List<string> ParseCsvLine(string line, char sep)
        {
            var outList = new List<string>();
            if (line == null) { outList.Add(""); return outList; }
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '\"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '\"') { sb.Append('\"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '\"') inQuotes = true;
                    else if (c == sep) { outList.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            outList.Add(sb.ToString());
            return outList;
        }

        // Caly plik jako rekordy CSV - pola w cudzyslowie moga zawierac nowe linie
        // (np. notatki z KeePassXC), wiec nie wolno dzielic pliku na linie przed parsowaniem.
        static List<List<string>> ParseCsvRecords(string text, char sep)
        {
            var records = new List<List<string>>();
            var row = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false, any = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '\"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\"') { sb.Append('\"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                    continue;
                }
                if (c == '\"') { inQuotes = true; any = true; }
                else if (c == sep) { row.Add(sb.ToString()); sb.Clear(); any = true; }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(sb.ToString()); sb.Clear();
                    if (any || row.Count > 1 || row[0].Length > 0) records.Add(row);
                    row = new List<string>(); any = false;
                }
                else { sb.Append(c); any = true; }
            }
            row.Add(sb.ToString());
            if (any || row.Count > 1 || row[0].Length > 0) records.Add(row);
            return records;
        }

        static char DetectSeparator(string header)
        {
            int c = header.Count(ch => ch == ',');
            int s = header.Count(ch => ch == ';');
            int t = header.Count(ch => ch == '\t');
            if (t >= c && t >= s && t > 0) return '\t';
            if (s > c) return ';';
            return ',';
        }

        static string Canon(string x)
        {
            return (x ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        }

        static string GetCol(Dictionary<string, int> map, List<string> row, params string[] keys)
        {
            foreach (var k in keys)
            {
                int idx;
                if (!map.TryGetValue(Canon(k), out idx)) continue;
                if (idx >= 0 && idx < row.Count)
                {
                    var value = (row[idx] ?? "").Trim();
                    if (value.Length > 0) return value;
                }
            }
            return "";
        }

        static string EnsureUrlScheme(string url)
        {
            var u = (url ?? "").Trim();
            if (u.Length == 0) return "";
            if (Uri.TryCreate(u, UriKind.Absolute, out var absolute))
                return (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps) && IsUsableWebHost(absolute.Host) ? absolute.ToString() : "";
            if (u.Contains("://", StringComparison.Ordinal) || u.Contains(' ') || u.Contains('\\')) return "";
            if (u.StartsWith("//", StringComparison.Ordinal)) u = u.Substring(2);
            var hostPart = u.Split('/')[0];
            if (hostPart.Length == 0 || (hostPart.IndexOf('.') < 0 && !string.Equals(hostPart, "localhost", StringComparison.OrdinalIgnoreCase))) return "";
            var candidate = (string.Equals(hostPart, "localhost", StringComparison.OrdinalIgnoreCase) ? "http://" : "https://") + u;
            return Uri.TryCreate(candidate, UriKind.Absolute, out var web) &&
                     (web.Scheme == Uri.UriSchemeHttp || web.Scheme == Uri.UriSchemeHttps) && IsUsableWebHost(web.Host) ? web.ToString() : "";
        }

        static string GetPasswordEntryHost(SavedPasswordEntry entry)
        {
            if (entry == null) return "";
            var fromUrl = HostFromAnyUrl(entry.Url);
            if (fromUrl.Length > 0) return NormalizeHostForMatch(fromUrl);
            var stored = NormalizeHostForMatch(entry.Host);
            if (stored == "localhost" || stored.Contains('.')) return stored;
            return "";
        }

        static string CompactPasswordIdentity(string value)
        {
            return new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        static bool PasswordEntryMatchesHost(SavedPasswordEntry entry, string host)
        {
            var target = NormalizeHostForMatch(host);
            var entryHost = GetPasswordEntryHost(entry);
            if (entryHost.Length > 0 &&
                (entryHost == target || target.EndsWith("." + entryHost, StringComparison.OrdinalIgnoreCase) || entryHost.EndsWith("." + target, StringComparison.OrdinalIgnoreCase)))
                return true;

            var labels = target.Split('.');
            var brand = labels.Length > 1 ? labels[labels.Length - 2] : labels[0];
            if (brand.Length < 4) return false;
            var identity = CompactPasswordIdentity((entry.Name ?? "") + " " + (entry.Url ?? "") + " " + (entry.Host ?? ""));
            return identity.Contains(CompactPasswordIdentity(brand), StringComparison.Ordinal);
        }

        void UpsertPassword(SavedPasswordEntry e)
        {
            EnsurePasswordVaultLoaded();
            var host = (e.Host ?? "").ToLowerInvariant();
            var user = (e.Username ?? "").Trim();
            var url = (e.Url ?? "").Trim();

            var existing = _passwordEntries.FirstOrDefault(x =>
                string.Equals((x.Host ?? "").ToLowerInvariant(), host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((x.Username ?? "").Trim(), user, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((x.Url ?? "").Trim(), url, StringComparison.OrdinalIgnoreCase) &&
                // wpisy bez adresu (czeste w KeePassXC) rozrozniamy po nazwie - inaczej nadpisywaly sie nawzajem
                (url.Length > 0 || string.Equals((x.Name ?? "").Trim(), (e.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)));

            if (existing == null)
            {
                e.UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _passwordEntries.Add(e);
                return;
            }

            existing.Name = e.Name;
            existing.Password = e.Password;
            existing.Notes = e.Notes;
            existing.Source = e.Source;
            existing.UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        int ImportPasswordsFromCsv(string filePath)
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8).TrimStart('\uFEFF');
            int firstBreak = text.IndexOfAny(new[] { '\r', '\n' });
            var sep = DetectSeparator(firstBreak >= 0 ? text.Substring(0, firstBreak) : text);
            var records = ParseCsvRecords(text, sep).Where(rw => rw.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
            if (records.Count == 0) return 0;

            var header = records[0];
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
            {
                var key = Canon(header[i]);
                if (key.Length > 0 && !map.ContainsKey(key)) map[key] = i;
            }
            // plik bez naglowka (url,uzytkownik,haslo)
            bool hasHeader = map.ContainsKey("password") || map.ContainsKey("loginpassword") || map.ContainsKey("haslo") || map.ContainsKey("hasło");
            if (!hasHeader) map.Clear();

            int added = 0;
            for (int i = hasHeader ? 1 : 0; i < records.Count; i++)
            {
                var row = records[i];
                // KeePass/KeePassXC: pomijamy wpisy z kosza
                var group = GetCol(map, row, "group", "folder", "grupa");
                if (group.IndexOf("Recycle Bin", StringComparison.OrdinalIgnoreCase) >= 0 || group.IndexOf("Kosz", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var rawUrl = GetCol(map, row, "url", "website", "web site", "login_uri", "hostname", "origin", "formactionorigin", "form_action_origin", "adres");
                var url = EnsureUrlScheme(rawUrl);
                if (url.Length == 0) url = rawUrl;
                var user = GetCol(map, row, "username", "user name", "login name", "login_username", "login", "user", "email", "uzytkownik", "użytkownik");
                var pass = GetCol(map, row, "password", "login_password", "haslo", "hasło");
                var name = GetCol(map, row, "name", "title", "account", "nazwa", "tytul", "tytuł");
                var notes = GetCol(map, row, "notes", "note", "comments", "extra", "notatki");

                if (!hasHeader && row.Count >= 3)
                {
                    url = EnsureUrlScheme((row[0] ?? "").Trim());
                    user = (row[1] ?? "").Trim();
                    pass = (row[2] ?? "").Trim();
                }

                if (pass.Length == 0) continue;
                if (url.StartsWith("{", StringComparison.Ordinal) || url.StartsWith("cmd://", StringComparison.OrdinalIgnoreCase)) url = "";
                var host = HostFromAnyUrl(url);

                var entry = new SavedPasswordEntry
                {
                    Name = name.Length > 0 ? name : host,
                    Url = url,
                    Host = host,
                    Username = user,
                    Password = pass,
                    Notes = notes,
                    Source = Path.GetFileName(filePath),
                    UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                UpsertPassword(entry);
                added++;
            }

            if (added > 0) SavePasswordVault();
            return added;
        }

        void ExportPasswordsToCsv(string filePath)
        {
            EnsurePasswordVaultLoaded();
            var sb = new StringBuilder();
            sb.AppendLine("name,url,username,password,note");
            foreach (var e in _passwordEntries.OrderBy(x => x.Host, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    CsvEscape(e.Name),
                    CsvEscape(e.Url),
                    CsvEscape(e.Username),
                    CsvEscape(e.Password),
                    CsvEscape(e.Notes)
                }));
            }
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        }

        void ImportPasswordsCsvWithDialog(Window owner)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = Przegladarka.L.T("Import haseł"),
                    Filter = "Pliki CSV/TSV (*.csv;*.txt)|*.csv;*.txt|Wszystkie pliki (*.*)|*.*"
                };
                if (dlg.ShowDialog(owner ?? this) != true) return;
                int n = ImportPasswordsFromCsv(dlg.FileName);
                MessageBox.Show(owner ?? this, Przegladarka.L.T("Zaimportowano wpisów: ") + n + ".", Przegladarka.L.T("Hasła"));
            }
            catch (Exception ex) { MessageBox.Show(owner ?? this, ex.Message, Przegladarka.L.T("Hasła")); }
        }

        void ExportPasswordsCsvWithDialog(Window owner)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = Przegladarka.L.T("Eksport haseł"),
                    Filter = "CSV (*.csv)|*.csv",
                    FileName = "velivo-passwords.csv"
                };
                if (dlg.ShowDialog(owner ?? this) != true) return;
                ExportPasswordsToCsv(dlg.FileName);
                MessageBox.Show(owner ?? this, Przegladarka.L.T("Wyeksportowano."), Przegladarka.L.T("Hasła"));
            }
            catch (Exception ex) { MessageBox.Show(owner ?? this, ex.Message, Przegladarka.L.T("Hasła")); }
        }

        int DeleteAllSavedPasswords()
        {
            EnsurePasswordVaultLoaded();
            int n = _passwordEntries.Count;
            _passwordEntries.Clear();
            SavePasswordVault();
            return n;
        }

        void DeleteSelectedPasswords(IEnumerable<SavedPasswordEntry> entries)
        {
            EnsurePasswordVaultLoaded();
            var set = new HashSet<SavedPasswordEntry>(entries ?? Enumerable.Empty<SavedPasswordEntry>());
            if (set.Count == 0) return;
            _passwordEntries = _passwordEntries.Where(x => !set.Contains(x)).ToList();
            SavePasswordVault();
        }

        static string MaskPassword(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            return new string('*', Math.Min(14, Math.Max(6, p.Length)));
        }

        static string GenerateStrongPassword(int len)
        {
            const string low = "abcdefghijkmnopqrstuvwxyz";
            const string upp = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string dig = "23456789";
            const string sym = "!@#$%^&*()-_=+[]{};:,.?";
            string all = low + upp + dig + sym;

            if (len < 10) len = 10;
            var chars = new List<char>
            {
                low[RandomNumberGenerator.GetInt32(low.Length)],
                upp[RandomNumberGenerator.GetInt32(upp.Length)],
                dig[RandomNumberGenerator.GetInt32(dig.Length)],
                sym[RandomNumberGenerator.GetInt32(sym.Length)]
            };
            for (int i = chars.Count; i < len; i++) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
            for (int i = chars.Count - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                var t = chars[i]; chars[i] = chars[j]; chars[j] = t;
            }
            return new string(chars.ToArray());
        }

        static SavedPasswordEntry ClonePassword(SavedPasswordEntry x)
        {
            if (x == null) return new SavedPasswordEntry();
            return new SavedPasswordEntry
            {
                Name = x.Name,
                Url = x.Url,
                Host = x.Host,
                Username = x.Username,
                Password = x.Password,
                Notes = x.Notes,
                Source = x.Source,
                UpdatedUnix = x.UpdatedUnix
            };
        }

                static string NormalizeHostForMatch(string host)
                {
                    host = (host ?? "").Trim().ToLowerInvariant();
                    if (host.StartsWith("www.")) host = host.Substring(4);
                    return host;
                }

                List<SavedPasswordEntry> FindPasswordsForHost(string host)
                {
                    EnsurePasswordVaultLoaded();
                    host = NormalizeHostForMatch(host);
                    if (host.Length == 0) return new List<SavedPasswordEntry>();
                    return _passwordEntries
                        .Where(e =>
                        {
                            return PasswordEntryMatchesHost(e, host);
                        })
                        .OrderByDescending(e => GetPasswordEntryHost(e).Length)
                        .ThenBy(e => e.Username ?? "", StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                static string BuildFillScript(string user, string pass)
                {
                        return @"(function(login, haslo) {
    const pola = [...document.querySelectorAll('input:not([type=""hidden""]), textarea')].filter(p => !p.disabled && p.getClientRects().length > 0);
    const poleHasla = pola.find(p => p.type === 'password' || /current-password/i.test(p.autocomplete || ''));
    const poleLogin = pola.find(p => p !== poleHasla && /username|email|user|login/i.test(`${p.name} ${p.id} ${p.autocomplete} ${p.type}`)) ||
        pola.find(p => p !== poleHasla && (p.type === 'text' || p.type === 'email')) || pola[0];
    function ustaw(pole, w) {
        if (!pole) return false;
        const proto = pole instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        const set = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
        if (set) set.call(pole, w); else pole.value = w;
        pole.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
        pole.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
        return true;
    }
    const a = login ? (poleLogin ? ustaw(poleLogin, login) : true) : true;
    const b = ustaw(poleHasla, haslo);
    try {
        const uu = poleLogin ? (poleLogin.value || '') : '';
        const pp = poleHasla ? (poleHasla.value || '') : '';
        if (pp) window.__velivoPwdCandidate = JSON.stringify({ username: uu, password: pp, url: location.href, source: 'fill' });
    } catch(e) {}
    return a && b;
})(" + JsonSerializer.Serialize(user ?? "") + "," + JsonSerializer.Serialize(pass ?? "") + ");";
                }

                // wpis z linkiem aplikacji Android (android://...@com.firma.app/): strona z pola Nazwa
                // (np. "maps.app.here.com"), a gdy jej brak - z odwroconej nazwy pakietu, jak robi to KeePassXC
                static string WebUrlForAndroidEntry(SavedPasswordEntry entry)
                {
                    var name = (entry.Name ?? "").Trim();
                    if (name.Length > 0 && !name.Contains(" ") && name.Contains(".") && Uri.CheckHostName(name.Split('/')[0]) == UriHostNameType.Dns)
                        return "https://" + name;
                    var raw = (entry.Url ?? "").Trim();
                    if (!raw.StartsWith("android://", StringComparison.OrdinalIgnoreCase)) return "";
                    var pkg = raw.Substring(raw.LastIndexOf('@') + 1).Trim('/');
                    if (pkg.StartsWith("android://", StringComparison.OrdinalIgnoreCase)) pkg = pkg.Substring(10);
                    var parts = pkg.Split('.').Where(x => x.Length > 0).Reverse().ToArray();
                    if (parts.Length < 2) return "";
                    var host = string.Join(".", parts);
                    return Uri.CheckHostName(host) == UriHostNameType.Dns ? "https://" + host : "";
                }

                void OpenPasswordEntryAndFill(SavedPasswordEntry entry)
                {
                        if (entry == null) return;
                        var url = EnsureUrlScheme((entry.Url ?? "").Trim());
                    if (url.Length == 0) url = EnsureUrlScheme(GetPasswordEntryHost(entry));
                    if (url.Length == 0) url = WebUrlForAndroidEntry(entry);
                    if (url.Length == 0)
                    {
                        MessageBox.Show(this, Przegladarka.L.T("Ten wpis nie zawiera adresu strony WWW. Zaimportowany link aplikacji Android nie może być otwarty w przeglądarce. Uzupełnij pole URL adresem https://..."), Przegladarka.L.T("Hasła"), MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                        _pendingFillHost = NormalizeHostForMatch(HostFromAnyUrl(url));
                        _pendingFillUser = entry.Username ?? "";
                        _pendingFillPass = entry.Password ?? "";
                        _pendingFillUntilUtc = DateTime.UtcNow.AddMinutes(2);
                        AddTab(url);
                }

                async System.Threading.Tasks.Task TryApplyPendingFill(CoreWebView2 core)
                {
                        try
                        {
                                if (core == null) return;
                                if (DateTime.UtcNow > _pendingFillUntilUtc)
                                {
                                        _pendingFillHost = _pendingFillUser = _pendingFillPass = null;
                                        return;
                                }
                                var host = NormalizeHostForMatch(HostFromAnyUrl(core.Source));
                                                                if (string.IsNullOrWhiteSpace(_pendingFillHost) ||
                                                                        !(string.Equals(host, _pendingFillHost, StringComparison.OrdinalIgnoreCase) ||
                                                                            host.EndsWith("." + _pendingFillHost, StringComparison.OrdinalIgnoreCase) ||
                                                                            _pendingFillHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))) return;
                                for (int attempt = 0; attempt < 12 && DateTime.UtcNow <= _pendingFillUntilUtc; attempt++)
                                {
                                    var result = await core.ExecuteScriptAsync(BuildFillScript(_pendingFillUser, _pendingFillPass));
                                    if (bool.TryParse(result, out var applied) && applied)
                                    {
                                        _pendingFillHost = _pendingFillUser = _pendingFillPass = null;
                                        return;
                                    }
                                    await System.Threading.Tasks.Task.Delay(350);
                                }
                        }
                        catch (Exception ex) { App.LogError(ex); }
                }

                async System.Threading.Tasks.Task InjectPasswordIcons(CoreWebView2 core)
                {
                        try
                        {
                                if (core == null) return;
                                var host = NormalizeHostForMatch(HostFromAnyUrl(core.Source));
                                if (host.Length == 0) return;
                                var creds = FindPasswordsForHost(host).Select(x => new { u = x.Username ?? "", p = x.Password ?? "" }).Take(24).ToList();
                                var credsJson = JsonSerializer.Serialize(creds);

                                var js = @"(() => {
                    const creds = " + credsJson + @";
                    if (!Array.isArray(creds)) return;

                    if (!document.getElementById('__velivoPwdIconsStyle')) {
                        const st = document.createElement('style');
                        st.id = '__velivoPwdIconsStyle';
                        st.textContent = '.velivo-pwd-panel{position:fixed;z-index:2147483647;display:none;gap:6px;align-items:center;background:rgba(16,24,40,.95);border:1px solid #334155;border-radius:10px;padding:4px;box-shadow:0 6px 22px rgba(0,0,0,.28);}'+
                                         '.velivo-pwd-btn{border:1px solid #475569;background:#f8fafc;color:#0f172a;border-radius:8px;padding:2px 8px;font-size:12px;line-height:16px;cursor:pointer;}'+
                                         '.velivo-pwd-btn[disabled]{opacity:.45;cursor:default;}';
                        document.documentElement.appendChild(st);
                    }

                    let panel = document.getElementById('__velivoPwdPanel');
                    if (!panel) {
                        panel = document.createElement('div');
                        panel.id = '__velivoPwdPanel';
                        panel.className = 'velivo-pwd-panel';
                        panel.innerHTML = '<button type=button class=velivo-pwd-btn data-v=fill title=Wypelnij_z_bazy_Velivo>🔑</button><button type=button class=velivo-pwd-btn data-v=gen title=Wygeneruj_haslo>⚡</button>';
                        document.documentElement.appendChild(panel);
                    }

                    function fire(el){ if(!el) return; el.dispatchEvent(new Event('input',{bubbles:true,composed:true})); el.dispatchEvent(new Event('change',{bubbles:true,composed:true})); }
                    function setVal(el, v){ if(!el) return; const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; const set = Object.getOwnPropertyDescriptor(proto,'value')?.set; if(set) set.call(el,v); else el.value=v; fire(el); }
                    function gen(len){ const chars='abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%^&*()-_=+[]{};:,.?'; let out=''; for(let i=0;i<len;i++) out += chars[Math.floor(Math.random()*chars.length)]; return out; }

                    function inFormInputs(ref){
                        const root = ref?.form || document;
                        return Array.from(root.querySelectorAll('input:not([type=hidden]), textarea'));
                    }
                    function findPassByContext(ref){
                        if (!ref) return null;
                        if (ref.type === 'password' || /password/i.test(ref.autocomplete || '')) return ref;
                        const arr = inFormInputs(ref);
                        return arr.find(i => i.type === 'password' || /password/i.test(i.autocomplete || '')) || null;
                    }
                    function findUser(pass){
                        const root = pass?.form || document;
                        const arr = Array.from(root.querySelectorAll('input:not([type=hidden])')).filter(x => x !== pass);
                        return arr.find(i => /username|email|login|user/i.test(`${i.name} ${i.id} ${i.autocomplete} ${i.type}`)) || arr.find(i => i.type === 'email' || i.type === 'text') || null;
                    }

                    let activePass = null;
                    let activeAnchor = null;

                    function placePanel(anchor){
                        if (!anchor) { panel.style.display = 'none'; return; }
                        const r = anchor.getBoundingClientRect();
                        if (!r || !isFinite(r.top) || r.width < 2 || r.height < 2) { panel.style.display = 'none'; return; }
                        panel.style.display = 'inline-flex';
                        const pw = panel.getBoundingClientRect().width || 112;
                        const ph = panel.getBoundingClientRect().height || 28;
                        // obok pola po prawej (nie na polu - tam bywaja ikony innych programow, np. sejfu);
                        // gdy brak miejsca - pod polem
                        let top, left;
                        if (r.right + 6 + pw <= window.innerWidth - 6) { left = r.right + 6; top = r.top + (r.height - ph) / 2; }
                        else { left = r.right - pw; top = r.bottom + 4; }
                        panel.style.top = `${Math.max(6, Math.min(window.innerHeight - ph - 4, top))}px`;
                        panel.style.left = `${Math.max(6, Math.min(window.innerWidth - pw - 6, left))}px`;
                    }

                    function refreshState(anchorEl){
                        activeAnchor = anchorEl || document.activeElement;
                        activePass = findPassByContext(activeAnchor);
                        const fillBtn = panel.querySelector('[data-v=fill]');
                        if (fillBtn) {
                            fillBtn.disabled = creds.length === 0 || !activePass;
                            fillBtn.title = creds.length === 0 ? 'Brak zapisanych haseł dla tej domeny' : 'Wypełnij z bazy Velivo';
                        }
                        placePanel(activeAnchor && activePass ? activeAnchor : null);
                    }

                    panel.onclick = (ev) => {
                        const btn = ev.target && ev.target.closest('button[data-v]');
                        if (!btn || !activePass) return;
                        if (btn.dataset.v === 'fill') {
                            const u = findUser(activePass);
                            const current = (u?.value || '').trim().toLowerCase();
                            const picked = creds.find(c => current && (c.u||'').trim().toLowerCase() === current) || creds[0] || null;
                            if (!picked) return;
                            if (u && picked.u) setVal(u, picked.u);
                            setVal(activePass, picked.p || '');
                            try {
                                const uu = u ? (u.value || '') : '';
                                const pp = activePass.value || '';
                                if (pp) window.__velivoPwdCandidate = JSON.stringify({ username: uu, password: pp, url: location.href, source: 'icon-fill' });
                            } catch(e) {}
                            return;
                        }
                        if (btn.dataset.v === 'gen') {
                            const v = gen(16);
                            setVal(activePass, v);
                            const u = findUser(activePass);
                            const uu = u ? (u.value || '') : '';
                            try { window.__velivoPwdCandidate = JSON.stringify({ username: uu, password: v, url: location.href, source: 'generator' }); } catch(e) {}
                        }
                    };

                    document.addEventListener('focusin', (ev) => {
                        const el = ev.target;
                        if (!(el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement)) { panel.style.display = 'none'; return; }
                        refreshState(el);
                    }, true);

                    document.addEventListener('click', (ev) => {
                        const el = ev.target;
                        if (!(el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement)) return;
                        refreshState(el);
                    }, true);

                    window.addEventListener('scroll', () => placePanel(activeAnchor && activePass ? activeAnchor : null), true);
                    window.addEventListener('resize', () => placePanel(activeAnchor && activePass ? activeAnchor : null), true);

                    refreshState(document.activeElement);
                })();";

                                await core.ExecuteScriptAsync(js);
                        }
                        catch (Exception ex) { App.LogError(ex); }
                }

                async System.Threading.Tasks.Task UpdatePasswordHelperAsync(CoreWebView2 core)
                {
                    try
                    {
                        if (core == null) return;
                        var host = NormalizeHostForMatch(HostFromAnyUrl(core.Source));
                        if (host.Length == 0) return;
                        var credentials = FindPasswordsForHost(host)
                            .Select(x => new { u = x.Username ?? "", p = x.Password ?? "", n = x.Name ?? "", url = x.Url ?? "" })
                            .ToList();
                        var credentialsJson = JsonSerializer.Serialize(credentials);

                        var initResult = await core.ExecuteScriptAsync(PasswordVaultDocumentCreatedScript);
                        if (!TryLogPasswordHelperStatus(initResult, "initialization")) return;

                        var updateScript = """
                            (() => {
                            try {
                                const helper = window.__velivoPasswordHelper;
                                if (!helper || typeof helper.setCredentials !== 'function') return '';
                                helper.setCredentials(__VELIVO_CREDENTIALS__);
                                return JSON.stringify(helper.status());
                            } catch (_) { return ''; }
                            })();
                            """.Replace("__VELIVO_CREDENTIALS__", credentialsJson);
                        var updateResult = await core.ExecuteScriptAsync(updateScript);
                        TryLogPasswordHelperStatus(updateResult, "credential update");
                    }
                    catch (Exception ex) { App.LogError(ex); }
                }

                bool TryLogPasswordHelperStatus(string rawResult, string phase)
                {
                    try
                    {
                        var statusJson = JsonSerializer.Deserialize<string>(rawResult ?? "");
                        if (string.IsNullOrWhiteSpace(statusJson)) throw new JsonException("Empty status.");
                        using var status = JsonDocument.Parse(statusJson);
                        if (!status.RootElement.TryGetProperty("panelCreated", out _) ||
                            !status.RootElement.TryGetProperty("inputsSeen", out _) ||
                            !status.RootElement.TryGetProperty("activeCandidateDetected", out _))
                            throw new JsonException("Unexpected helper status shape.");
                        if (!status.RootElement.GetProperty("panelCreated").GetBoolean())
                            throw new InvalidOperationException("Password helper panel was not created.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        App.LogError(new InvalidOperationException("Password helper " + phase + " status could not be parsed.", ex));
                        return false;
                    }
                }

                async System.Threading.Tasks.Task CapturePasswordCandidateAndPrompt(BrowserTab tab, CoreWebView2 core)
                {
                    bool captureLockAcquired = false;
                        try
                        {
                    if (core == null) return;
                        captureLockAcquired = _passwordCaptureInProgress.Add(core);
                        if (!captureLockAcquired) return;

                                string js = """
                                    (() => {
                                        try {
                                            const helper = window.__velivoPasswordHelper;
                                            const candidate = helper && helper.takeCandidate();
                                            return candidate ? JSON.stringify(candidate) : '';
                                        } catch (_) { return ''; }
                                    })();
                                    """;

                                var raw = await core.ExecuteScriptAsync(js);
                                var payload = JsonSerializer.Deserialize<string>(raw);
                                if (string.IsNullOrWhiteSpace(payload)) return;
                                using (var doc = JsonDocument.Parse(payload))
                                {
                                        var root = doc.RootElement;
                                        var pass = root.TryGetProperty("password", out var pe) ? (pe.GetString() ?? "").Trim() : "";
                                        if (pass.Length < 6) return;
                                        var user = root.TryGetProperty("username", out var ue) ? (ue.GetString() ?? "").Trim() : "";
                                        var url = root.TryGetProperty("url", out var ur) ? (ur.GetString() ?? "") : core.Source;
                                        var host = NormalizeHostForMatch(HostFromAnyUrl(url));
                                        if (host.Length == 0) host = NormalizeHostForMatch(HostFromAnyUrl(core.Source));

                                        var source = root.TryGetProperty("source", out var se) ? (se.GetString() ?? "") : "";
                                        if (string.Equals(source, "fill", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (host.Length > 0)
                                            {
                                                _pendingFillHost = host;
                                                _pendingFillUser = user;
                                                _pendingFillPass = pass;
                                                _pendingFillUntilUtc = DateTime.UtcNow.AddMinutes(2);
                                            }
                                            return;
                                        }
                                        if (tab == null || tab.Private || _settings == null || !_settings.SavePasswords || host.Length == 0) return;

                                        EnsurePasswordVaultLoaded();
                                        string key = host + "|" + user.ToLowerInvariant();
                                        DateTime last;
                                        if (_passwordPrompted.TryGetValue(key, out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(2)) return;

                                        var existing = _passwordEntries.FirstOrDefault(x =>
                                            PasswordEntryMatchesHost(x, host) &&
                                                string.Equals((x.Username ?? "").Trim(), user, StringComparison.OrdinalIgnoreCase));

                                        if (existing != null && string.Equals((existing.Password ?? "").Trim(), pass, StringComparison.Ordinal)) return;
                                        _passwordPrompted[key] = DateTime.UtcNow;

                                        if (existing != null)
                                        {
                                                if (MessageBox.Show(this,
                                                        Przegladarka.L.T("Wykryto nowe hasło dla ") + (user.Length > 0 ? user : "(bez loginu)") + Przegladarka.L.T(" na ") + host + Przegladarka.L.T(".\nZaktualizować wpis w bazie Velivo?"),
                                                        Przegladarka.L.T("Hasła"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                                                existing.Password = pass;
                                                existing.Url = EnsureUrlScheme(url);
                                                existing.Source = "captured";
                                                existing.UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                                SavePasswordVault();
                                                return;
                                        }

                                        if (MessageBox.Show(this,
                                                Przegladarka.L.T("Wykryto hasło na stronie ") + host + Przegladarka.L.T(".\nDodać do bazy Velivo?"),
                                                Przegladarka.L.T("Hasła"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                                        UpsertPassword(new SavedPasswordEntry
                                        {
                                                Name = host,
                                                Url = EnsureUrlScheme(url),
                                                Host = NormalizeHostForMatch(host),
                                                Username = user,
                                                Password = pass,
                                                Notes = "",
                                                Source = "captured",
                                                UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                        });
                                        SavePasswordVault();
                                }
                        }
                        catch (Exception ex) { App.LogError(ex); }
                        finally { if (captureLockAcquired && core != null) _passwordCaptureInProgress.Remove(core); }
                }

                    async System.Threading.Tasks.Task HookPasswordVault(BrowserTab tab, CoreWebView2 core)
                {
                        if (tab == null || core == null) return;
                        if (_passwordVaultScriptsRegistered.Add(core))
                        {
                            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(PasswordVaultDocumentCreatedScript); }
                            catch (Exception ex)
                            {
                                _passwordVaultScriptsRegistered.Remove(core);
                                App.LogError(ex);
                            }
                        }

                        if (!_passwordVaultHooks.Add(core)) return;
                        core.NavigationCompleted += async (s, e) =>
                        {
                            if (!e.IsSuccess) return;
                            await UpdatePasswordHelperAsync(core);
                            await TryApplyPendingFill(core);
                        };

                        if (!tab.Private)
                        {
                            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                            timer.Tick += (s, e) =>
                            {
                                if (tab == _current && !_passwordCaptureInProgress.Contains(core))
                                    _ = CapturePasswordCandidateAndPrompt(tab, core);
                            };
                            _passwordCaptureTimers[core] = timer;
                            timer.Start();
                        }
                    }

                    void StopPasswordCapture(CoreWebView2 core)
                    {
                        if (core == null) return;
                        if (_passwordCaptureTimers.TryGetValue(core, out var timer))
                        {
                            timer.Stop();
                            _passwordCaptureTimers.Remove(core);
                        }
                        _passwordCaptureInProgress.Remove(core);
                        _passwordVaultHooks.Remove(core);
                        _passwordVaultScriptsRegistered.Remove(core);
                    }

        bool EditPasswordEntryDialog(Window owner, SavedPasswordEntry existing, out SavedPasswordEntry edited)
        {
            edited = null;
            var seed = ClonePassword(existing);
            var win = new Window
            {
                Title = existing == null ? Przegladarka.L.T("Dodaj wpis hasła") : Przegladarka.L.T("Edytuj wpis hasła"),
                Width = 580,
                Height = 460,
                MinWidth = 540,
                MinHeight = 420,
                Owner = owner ?? this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize
            };

            var root = new Grid { Margin = new Thickness(14) };
            for (int i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock L(string t) { return new TextBlock { Text = Przegladarka.L.T(t), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) }; }
            TextBox T(string v) { return new TextBox { Text = v ?? "", Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6, 4, 6, 4) }; }

            var name = T(seed.Name);
            var url = T(seed.Url);
            var user = T(seed.Username);
            var pass = T(seed.Password);
            var notes = new TextBox { Text = seed.Notes ?? "", AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 4, 6, 4) };

            var genRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var genBtn = SmallButton(Przegladarka.L.T("Generuj mocne hasło"), () => pass.Text = GenerateStrongPassword(16));
            var pasteBtn = SmallButton(Przegladarka.L.T("Wklej ze schowka"), () => { try { if (Clipboard.ContainsText()) pass.Text = Clipboard.GetText(); } catch (Exception) { } });
            genRow.Children.Add(genBtn);
            genRow.Children.Add(pasteBtn);

            Grid.SetRow(L("Nazwa:"), 0); Grid.SetColumn(L("Nazwa:"), 0);
            var l0 = L("Nazwa:"); Grid.SetRow(l0, 0); Grid.SetColumn(l0, 0); root.Children.Add(l0);
            Grid.SetRow(name, 0); Grid.SetColumn(name, 1); root.Children.Add(name);

            var l1 = L("URL / strona:"); Grid.SetRow(l1, 1); Grid.SetColumn(l1, 0); root.Children.Add(l1);
            Grid.SetRow(url, 1); Grid.SetColumn(url, 1); root.Children.Add(url);

            var l2 = L(Przegladarka.L.T("Użytkownik:")); Grid.SetRow(l2, 2); Grid.SetColumn(l2, 0); root.Children.Add(l2);
            Grid.SetRow(user, 2); Grid.SetColumn(user, 1); root.Children.Add(user);

            var l3 = L(Przegladarka.L.T("Hasło:")); Grid.SetRow(l3, 3); Grid.SetColumn(l3, 0); root.Children.Add(l3);
            Grid.SetRow(pass, 3); Grid.SetColumn(pass, 1); root.Children.Add(pass);

            Grid.SetRow(genRow, 4); Grid.SetColumn(genRow, 1); root.Children.Add(genRow);

            var l4 = L("Notatki:"); Grid.SetRow(l4, 5); Grid.SetColumn(l4, 0); root.Children.Add(l4);
            Grid.SetRow(notes, 6); Grid.SetColumn(notes, 1); root.Children.Add(notes);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            bool ok = false;
            var saveBtn = new Button { Content = Przegladarka.L.T("Zapisz"), Width = 96, Height = 30, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancelBtn = new Button { Content = Przegladarka.L.T("Anuluj"), Width = 96, Height = 30, IsCancel = true };
            saveBtn.Click += (s, e) => { ok = true; win.Close(); };
            cancelBtn.Click += (s, e) => win.Close();
            btns.Children.Add(saveBtn);
            btns.Children.Add(cancelBtn);
            Grid.SetRow(btns, 7); Grid.SetColumn(btns, 1); root.Children.Add(btns);

            win.Content = root;
            win.ShowDialog();
            if (!ok) return false;

            var p = (pass.Text ?? "").Trim();
            if (p.Length == 0)
            {
                MessageBox.Show(owner ?? this, Przegladarka.L.T("Hasło nie może być puste."), Przegladarka.L.T("Hasła"));
                return false;
            }

            var u = EnsureUrlScheme((url.Text ?? "").Trim());
            edited = new SavedPasswordEntry
            {
                Name = (name.Text ?? "").Trim(),
                Url = u,
                Host = HostFromAnyUrl(u),
                Username = (user.Text ?? "").Trim(),
                Password = p,
                Notes = (notes.Text ?? "").Trim(),
                Source = string.IsNullOrWhiteSpace(seed.Source) ? "manual" : seed.Source,
                UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            if (string.IsNullOrWhiteSpace(edited.Host) && !string.IsNullOrWhiteSpace(edited.Url)) edited.Host = edited.Url;
            return true;
        }

        void OpenPasswordsManager(Window owner)
        {
            EnsurePasswordVaultLoaded();

            var win = new Window
            {
                Title = Przegladarka.L.T("Menedżer haseł Velivo"),
                Width = 920,
                Height = 620,
                MinWidth = 780,
                MinHeight = 520,
                Owner = owner ?? this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var root = new DockPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };
            var info = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var search = new TextBox { Width = 260, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Przegladarka.L.T("Szukaj po domenie, loginie lub nazwie") };

            var list = new ListView { Margin = new Thickness(10, 0, 10, 8), SelectionMode = SelectionMode.Extended };
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn { Header = "Domena", DisplayMemberBinding = new System.Windows.Data.Binding("Host"), Width = 210 });
            gv.Columns.Add(new GridViewColumn { Header = "Użytkownik", DisplayMemberBinding = new System.Windows.Data.Binding("Username"), Width = 210 });
            gv.Columns.Add(new GridViewColumn { Header = "Nazwa", DisplayMemberBinding = new System.Windows.Data.Binding("Name"), Width = 210 });
            gv.Columns.Add(new GridViewColumn { Header = "Źródło", DisplayMemberBinding = new System.Windows.Data.Binding("Source"), Width = 110 });
            gv.Columns.Add(new GridViewColumn { Header = "Zmieniono", DisplayMemberBinding = new System.Windows.Data.Binding("Updated"), Width = 120 });
            list.View = gv;

            var details = new TextBox
            {
                Margin = new Thickness(10, 0, 10, 10),
                IsReadOnly = true,
                MinHeight = 84,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            string currentFilter = "";
            bool reveal = false;

            Action refresh = null;
            Action updateDetails = null;

            refresh = () =>
            {
                list.Items.Clear();
                var q = (currentFilter ?? "").Trim().ToLowerInvariant();
                IEnumerable<SavedPasswordEntry> rows = _passwordEntries;
                if (q.Length > 0)
                    rows = rows.Where(x =>
                        GetPasswordEntryHost(x).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        ((x.Host ?? "").ToLowerInvariant().Contains(q)) ||
                        ((x.Url ?? "").ToLowerInvariant().Contains(q)) ||
                        ((x.Username ?? "").ToLowerInvariant().Contains(q)) ||
                        ((x.Name ?? "").ToLowerInvariant().Contains(q)) ||
                        ((x.Notes ?? "").ToLowerInvariant().Contains(q)) ||
                        ((x.Source ?? "").ToLowerInvariant().Contains(q)));

                foreach (var e in rows.OrderBy(x => x.Host, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase))
                {
                    list.Items.Add(new
                    {
                        Host = GetPasswordEntryHost(e),
                        Username = e.Username,
                        Name = e.Name,
                        Source = e.Source,
                        Updated = e.UpdatedUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.UpdatedUnix).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "",
                        Entry = e
                    });
                }
                info.Text = Przegladarka.L.T("Wpisów: ") + _passwordEntries.Count + " | Widoczne: " + list.Items.Count;
                updateDetails();
            };

            updateDetails = () =>
            {
                dynamic row = list.SelectedItem;
                if (row == null)
                {
                    details.Text = "";
                    return;
                }
                SavedPasswordEntry e = row.Entry as SavedPasswordEntry;
                if (e == null)
                {
                    details.Text = "";
                    return;
                }
                details.Text = Przegladarka.L.T("Domena: ") + (e.Host ?? "") +
                    "\nURL: " + (e.Url ?? "") +
                    Przegladarka.L.T("\nUżytkownik: ") + (e.Username ?? "") +
                    Przegladarka.L.T("\nHasło: ") + (reveal ? (e.Password ?? "") : MaskPassword(e.Password)) +
                    Przegladarka.L.T("\nNazwa: ") + (e.Name ?? "") +
                    Przegladarka.L.T("\nNotatki: ") + (e.Notes ?? "");
            };

            var addBtn = SmallButton(Przegladarka.L.T("Dodaj"), () =>
            {
                try
                {
                    SavedPasswordEntry ne;
                    if (!EditPasswordEntryDialog(win, null, out ne)) return;
                    ne.Source = "manual";
                    UpsertPassword(ne);
                    SavePasswordVault();
                    refresh();
                }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, Przegladarka.L.T("Hasła")); }
            });

            var editBtn = SmallButton(Przegladarka.L.T("Edytuj"), () =>
            {
                dynamic row = list.SelectedItem;
                if (row == null) return;
                var cur = row.Entry as SavedPasswordEntry;
                if (cur == null) return;
                try
                {
                    SavedPasswordEntry ne;
                    if (!EditPasswordEntryDialog(win, cur, out ne)) return;
                    var idx = _passwordEntries.IndexOf(cur);
                    if (idx >= 0)
                    {
                        ne.Source = "manual";
                        _passwordEntries[idx] = ne;
                        SavePasswordVault();
                    }
                    refresh();
                }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, Przegladarka.L.T("Hasła")); }
            });

            var delSelBtn = SmallButton(Przegladarka.L.T("Usuń zaznaczone"), () =>
            {
                var selected = list.SelectedItems.Cast<dynamic>().Select(x => x.Entry as SavedPasswordEntry).Where(x => x != null).ToList();
                if (selected.Count == 0) return;
                if (MessageBox.Show(win,
                    Przegladarka.L.T("Usunąć zaznaczone wpisy: ") + selected.Count + "?",
                    Przegladarka.L.T("Hasła"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try
                {
                    DeleteSelectedPasswords(selected);
                    refresh();
                }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, Przegladarka.L.T("Hasła")); }
            });

            var copyUserBtn = SmallButton(Przegladarka.L.T("Kopiuj login"), () =>
            {
                dynamic row = list.SelectedItem;
                if (row == null) return;
                var e = row.Entry as SavedPasswordEntry;
                if (e == null || string.IsNullOrEmpty(e.Username)) return;
                try { Clipboard.SetText(e.Username); } catch (Exception) { }
            });

            var copyPassBtn = SmallButton(Przegladarka.L.T("Kopiuj hasło"), () =>
            {
                dynamic row = list.SelectedItem;
                if (row == null) return;
                var e = row.Entry as SavedPasswordEntry;
                if (e == null || string.IsNullOrEmpty(e.Password)) return;
                try { Clipboard.SetText(e.Password); } catch (Exception) { }
            });

            var revealBtn = SmallButton(Przegladarka.L.T("Pokaż/ukryj hasło"), () => { reveal = !reveal; updateDetails(); });

            var importBtn = SmallButton(Przegladarka.L.T("Import CSV…"), () => { ImportPasswordsCsvWithDialog(win); EnsurePasswordVaultLoaded(); refresh(); });
            var exportBtn = SmallButton(Przegladarka.L.T("Eksport CSV…"), () => ExportPasswordsCsvWithDialog(win));

            var deleteAllBtn = SmallButton(Przegladarka.L.T("Usuń wszystkie zapisane hasła"), () =>
            {
                if (MessageBox.Show(win,
                    Przegladarka.L.T("Usunąć hurtowo wszystkie hasła z lokalnej bazy Velivo?\nOperacji nie da się cofnąć."),
                    Przegladarka.L.T("Hasła"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try
                {
                    int n = DeleteAllSavedPasswords();
                    refresh();
                    MessageBox.Show(win, Przegladarka.L.T("Usunięto wpisów: ") + n + ".", Przegladarka.L.T("Hasła"));
                }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, Przegladarka.L.T("Hasła")); }
            });

            search.TextChanged += (s, e) => { currentFilter = search.Text ?? ""; refresh(); };
            list.SelectionChanged += (s, e) => updateDetails();
            list.MouseDoubleClick += (s, e) =>
            {
                dynamic row = list.SelectedItem;
                if (row == null) return;
                var cur = row.Entry as SavedPasswordEntry;
                if (cur == null) return;
                OpenPasswordEntryAndFill(cur);
                win.Close();
            };

            top.Children.Add(addBtn);
            top.Children.Add(editBtn);
            top.Children.Add(delSelBtn);
            top.Children.Add(copyUserBtn);
            top.Children.Add(copyPassBtn);
            top.Children.Add(revealBtn);
            top.Children.Add(importBtn);
            top.Children.Add(exportBtn);
            top.Children.Add(deleteAllBtn);
            top.Children.Add(search);
            top.Children.Add(info);

            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(details, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(details);
            root.Children.Add(list);
            win.Content = root;
            refresh();
            win.ShowDialog();
        }
    }
}
