// ============================================
// SHARED COMPONENTS - PUBLIC EXPORTS
// ============================================

// Core UI Components
export { Avatar, type AvatarProps, type AvatarSize, type AvatarVariant } from './Avatar';
export {
  Divider,
  type DividerProps,
  type DividerSpacing,
  type DividerThickness,
  type DividerColor,
} from './Divider';

// Form Components
export { AmountInput, type AmountInputProps } from './AmountInput';

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
export { BottomSheet, type BottomSheetProps } from './BottomSheet';
export { ConfirmDialog, type ConfirmDialogProps } from './ConfirmDialog';
