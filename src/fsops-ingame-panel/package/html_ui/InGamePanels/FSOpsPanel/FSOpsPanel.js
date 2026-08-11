/**
 * Registers the FSOps in-game panel custom element and points its iframe at FSOps' own local
 * server. Adapted from the verified working reference this package is built from
 * (bymaximus/msfs2020-toolbar-window-template's CustomPanel, same "toolbar iframe loads
 * localhost" technique the plan cites for Navigraph's panel and buzinin/msfs2024-efb-panel) -
 * see ../../../../README.md for the full trail. The only behavioural change from that reference
 * is the source URL: instead of a fixed external site, it reads the port FSOps is currently
 * listening on from FSOpsPanel.config.js (rewritten by the app's install/update/repair operation -
 * see PanelPackageInstaller.cs) and falls back to FSOps' documented default port, 5977, if that
 * file is ever missing or hasn't loaded.
 *
 * The iframe src is only ever set while the panel is actually open (panelActive) and cleared the
 * moment it closes (panelInactive) - the same lifecycle the reference uses, so a closed panel
 * doesn't sit there polling FSOps' SignalR hub for no reason.
 */
class IngamePanelFSOpsPanel extends TemplateElement {
    constructor() {
        super(...arguments);
        this.ingameUi = null;
        this.iframeElement = null;
        this.started = false;
        this.initialize();
    }

    connectedCallback() {
        super.connectedCallback();

        const self = this;
        this.ingameUi = this.querySelector('ingame-ui');
        this.iframeElement = document.getElementById('FSOpsPanelIframe');

        if (this.ingameUi) {
            this.ingameUi.addEventListener('panelActive', () => {
                if (self.iframeElement) {
                    self.iframeElement.src = self.panelUrl();
                }
            });
            this.ingameUi.addEventListener('panelInactive', () => {
                if (self.iframeElement) {
                    self.iframeElement.src = '';
                }
            });
        }
    }

    /** window.FSOPS_PANEL_PORT comes from FSOpsPanel.config.js - see that file's header comment for
     *  why the port lives there and nowhere else in this package. 5977 (FSOps' documented default -
     *  see Program.cs) is the fallback if that file is ever missing, fails to load, or predates this
     *  variable. */
    panelUrl() {
        const port = (typeof window.FSOPS_PANEL_PORT === 'number') ? window.FSOPS_PANEL_PORT : 5977;
        return 'http://localhost:' + port + '/panel';
    }

    initialize() {
        if (this.started) {
            return;
        }
        this.started = true;
    }

    disconnectedCallback() {
        super.disconnectedCallback();
    }
}

window.customElements.define('ingamepanel-fsops', IngamePanelFSOpsPanel);
checkAutoload();
