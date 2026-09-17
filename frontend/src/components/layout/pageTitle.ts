/**
 * A page's document title: its own name first, so a tab strip or a screen reader's window list
 * tells pages apart (WCAG 2.4.2). `index.html` keeps the bare "AzureBank" for the moment before
 * the app has rendered anything. A `.ts` file, not part of `RouteAnnouncer.tsx`, because a `.tsx`
 * here exports components or helpers, never both (`react-refresh/only-export-components`).
 */
export const pageTitle = (name: string) => `${name} · AzureBank`;
