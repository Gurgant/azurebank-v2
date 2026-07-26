// ============================================
// SHARED COMPONENTS - PUBLIC EXPORTS
// ============================================

// Core UI Components
export { Avatar, type AvatarProps, type AvatarSize, type AvatarVariant } from './Avatar';

// Icon Components
export {
  IconContainer,
  type IconContainerProps,
  type IconContainerVariant,
  type IconContainerSize,
} from './IconContainer';

// Transaction Components — the transaction TYPE is the contract enum now (api/enums),
// not a component-local alias, so the barrel exports only the component surface.
export { TransactionItem, type TransactionItemProps } from './TransactionItem';

// Action Components
export {
  QuickActionButton,
  type QuickActionButtonProps,
  type QuickActionVariant,
} from './QuickActionButton';

// Dialog Components
export { ConfirmDialog, type ConfirmDialogProps } from './ConfirmDialog';
