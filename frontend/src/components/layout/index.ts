// ============================================
// LAYOUT COMPONENTS - PUBLIC EXPORTS
// ============================================

export { AppLayout, type AppLayoutProps } from './AppLayout';
export { Header, type HeaderProps } from './Header';
export { BottomNav, type BottomNavProps } from './BottomNav';
export { Sidebar, type SidebarProps } from './Sidebar';
export { ProtectedRoute } from './ProtectedRoute';
export { ProtectedShell } from './ProtectedShell';

// The information architecture. Both nav surfaces read it, and PageHeader will read it too — which
// is the point: a page header that derives its title from the same table as the nav cannot drift
// from it.
export {
  NAV_PLACES,
  TRANSFER_ACTION,
  navCurrent,
  isNavPlaceActive,
  findActiveNavPlace,
  type NavPlace,
  type NavCurrent,
} from './navItems';
