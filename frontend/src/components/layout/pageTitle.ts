/**
 * A page's document title: its own name first, so a tab strip or a screen reader's window list
 * tells pages apart (WCAG 2.4.2). `index.html` keeps the bare "AzureBank" for the moment before
 * the app has rendered anything. A `.ts` file, not part of `RouteAnnouncer.tsx`, because a `.tsx`
 * here exports components or helpers, never both (`react-refresh/only-export-components`).
 */
export const pageTitle = (name: string) => `${name} · AzureBank`;

/**
 * How long after a route change `RouteAnnouncer` writes its announcement. The region is emptied at
 * the change and filled this much later, in a task of its own: a screen reader announces a CHANGE
 * to a live region, so the empty state has to be seen first, and the move of focus that happens at
 * the change should not land in the same instant as the text. 100ms is the upper end of what
 * live-region guidance uses for this.
 */
export const ROUTE_ANNOUNCE_DELAY_MS = 100;
