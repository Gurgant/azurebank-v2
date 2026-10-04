/*
  The demo's components, for the pages and the route that render them, and nothing else.

  The demo's helpers are not handed out here. The session middleware imports one of them, the
  storage module, and it imports the file itself (src/features/auth/sessionMiddleware.ts): through
  this barrel it would pull every component in behind the storage module, and with each component
  whatever that component imports. Inside src/features a helper is imported from its own file.
*/
export { ClosedInDemo } from './ClosedInDemo';
export { DemoEntry } from './DemoEntry';
export { StartOverDialog } from './StartOverDialog';
