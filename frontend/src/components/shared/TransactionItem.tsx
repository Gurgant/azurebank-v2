import { makeStyles, mergeClasses, Text } from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowSwap24Regular,
  ChevronRight24Regular,
} from '@fluentui/react-icons';
import { format } from 'date-fns';
import { colors, transitions } from '../../theme/tokens';
import { IconContainer } from './IconContainer';

// ============================================
// TYPES
// ============================================

export type TransactionType = 'deposit' | 'withdrawal' | 'transfer-in' | 'transfer-out';

export interface TransactionItemProps {
  /** Unique transaction ID */
  id: string;
  /** Transaction type */
  type: TransactionType;
  /** Amount (positive number) */
  amount: number;
  /** Description or title */
  description: string;
  /** Transaction date */
  date: Date;
  /** Recipient/sender name for transfers */
  counterparty?: string;
  /** Show navigation arrow */
  showArrow?: boolean;
  /** Click handler */
  onClick?: (id: string) => void;
  /** Additional CSS class */
  className?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    display: 'flex',
    alignItems: 'center',
    padding: '16px',
    gap: '12px',
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    transition: `background-color ${transitions.fast}`,
  },
  clickable: {
    cursor: 'pointer',
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  content: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },

  description: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[800],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  meta: {
    fontSize: '13px',
    color: colors.neutral[500],
  },

  amountContainer: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: '2px',
  },

  amount: {
    fontSize: '15px',
    fontWeight: 600,
  },
  amountPositive: {
    color: colors.semantic.success.main,
  },
  amountNegative: {
    color: colors.semantic.error.main,
  },

  typeLabel: {
    fontSize: '12px',
    color: colors.neutral[500],
  },

  arrow: {
    color: colors.neutral[400],
    flexShrink: 0,
  },
});

// ============================================
// HELPER FUNCTIONS
// ============================================

function getTransactionIcon(type: TransactionType) {
  switch (type) {
    case 'deposit':
      return <ArrowDownload24Regular />;
    case 'withdrawal':
      return <ArrowUpload24Regular />;
    case 'transfer-in':
    case 'transfer-out':
      return <ArrowSwap24Regular />;
    default:
      return <ArrowSwap24Regular />;
  }
}

function getIconVariant(type: TransactionType) {
  return type as 'deposit' | 'withdrawal' | 'transfer-in' | 'transfer-out';
}

function getTypeLabel(type: TransactionType): string {
  switch (type) {
    case 'deposit':
      return 'Deposit';
    case 'withdrawal':
      return 'Withdrawal';
    case 'transfer-in':
      return 'Transfer In';
    case 'transfer-out':
      return 'Transfer Out';
    default:
      return 'Transaction';
  }
}

function formatAmount(amount: number, type: TransactionType): string {
  const formatted = new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
  }).format(amount);

  const isPositive = type === 'deposit' || type === 'transfer-in';
  return isPositive ? `+${formatted}` : `-${formatted}`;
}

// ============================================
// COMPONENT
// ============================================

export function TransactionItem({
  id,
  type,
  amount,
  description,
  date,
  counterparty,
  showArrow = true,
  onClick,
  className,
}: TransactionItemProps) {
  const styles = useStyles();
  const isPositive = type === 'deposit' || type === 'transfer-in';

  const handleClick = () => {
    if (onClick) {
      onClick(id);
    }
  };

  return (
    <div
      className={mergeClasses(styles.container, onClick && styles.clickable, className)}
      onClick={handleClick}
      role={onClick ? 'button' : undefined}
      tabIndex={onClick ? 0 : undefined}
      onKeyDown={
        onClick
          ? (e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                handleClick();
              }
            }
          : undefined
      }
    >
      <IconContainer variant={getIconVariant(type)} size="md">
        {getTransactionIcon(type)}
      </IconContainer>

      <div className={styles.content}>
        <Text className={styles.description}>{counterparty || description}</Text>
        <Text className={styles.meta}>{format(date, 'MMM d, yyyy')}</Text>
      </div>

      <div className={styles.amountContainer}>
        <Text
          className={mergeClasses(
            styles.amount,
            isPositive ? styles.amountPositive : styles.amountNegative,
          )}
        >
          {formatAmount(amount, type)}
        </Text>
        <Text className={styles.typeLabel}>{getTypeLabel(type)}</Text>
      </div>

      {showArrow && onClick && <ChevronRight24Regular className={styles.arrow} />}
    </div>
  );
}

export default TransactionItem;
