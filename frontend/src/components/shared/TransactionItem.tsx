import { makeStyles, mergeClasses, Text } from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowSwap24Regular,
  ChevronRight24Regular,
} from '@fluentui/react-icons';
import { format } from 'date-fns';
import { colors, transitions } from '../../theme/tokens';
import type { TransactionType } from '../../api/enums';
import { formatTransactionAmount, isIncomeType } from '../../utils/format';
import { IconContainer, type IconContainerVariant } from './IconContainer';

// ============================================
// TYPES
// ============================================

export interface TransactionItemProps {
  /** Unique transaction ID (TransactionResponse.id) */
  id: string;
  /** Transaction type — the contract enum (PascalCase), never a display string */
  type: TransactionType;
  /** Amount as an UNSIGNED number (the API sends unsigned; the sign comes from `type`) */
  amount: number;
  /** Description or title */
  description: string;
  /** ISO 8601 timestamp (TransactionResponse.createdAt) */
  date: string;
  /** Recipient/sender label for transfers, shown in place of the description when present */
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
    fontVariantNumeric: 'tabular-nums',
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
    case 'Deposit':
      return <ArrowDownload24Regular />;
    case 'Withdrawal':
      return <ArrowUpload24Regular />;
    default:
      return <ArrowSwap24Regular />;
  }
}

function getIconVariant(type: TransactionType): IconContainerVariant {
  switch (type) {
    case 'Deposit':
      return 'deposit';
    case 'Withdrawal':
      return 'withdrawal';
    case 'TransferOut':
      return 'transfer-out';
    case 'TransferIn':
      return 'transfer-in';
    default:
      return 'neutral';
  }
}

function getTypeLabel(type: TransactionType): string {
  switch (type) {
    case 'Deposit':
      return 'Deposit';
    case 'Withdrawal':
      return 'Withdrawal';
    case 'TransferIn':
      return 'Transfer In';
    case 'TransferOut':
      return 'Transfer Out';
    default:
      return 'Transaction';
  }
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
  const positive = isIncomeType(type);

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
        <Text className={styles.description}>
          {counterparty || description || getTypeLabel(type)}
        </Text>
        <Text className={styles.meta}>{format(new Date(date), 'MMM d, yyyy')}</Text>
      </div>

      <div className={styles.amountContainer}>
        <Text
          className={mergeClasses(
            styles.amount,
            positive ? styles.amountPositive : styles.amountNegative,
          )}
        >
          {formatTransactionAmount(amount, type)}
        </Text>
        <Text className={styles.typeLabel}>{getTypeLabel(type)}</Text>
      </div>

      {showArrow && onClick && <ChevronRight24Regular className={styles.arrow} />}
    </div>
  );
}

export default TransactionItem;
