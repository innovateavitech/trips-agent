import './styles/tokens.css';

export { Alert, alertVariants, type AlertProps } from './components/alert';
export { Badge, badgeVariants, type BadgeProps } from './components/badge';
export { Button, buttonVariants, type ButtonProps } from './components/button';
export {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from './components/card';
export { Input, type InputProps } from './components/input';
export { Select, type SelectProps } from './components/select';
export {
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from './components/table';

/* Shared state components. Every screen shows one of these before it shows data,
   so they are built here once rather than reinvented per feature screen. */
export { Loading, type LoadingProps } from './components/loading';
export { Spinner, type SpinnerProps } from './components/spinner';
export { Skeleton, type SkeletonProps } from './components/skeleton';
export { EmptyState, type EmptyStateProps } from './components/empty-state';
export { ErrorState, type ErrorStateProps } from './components/error-state';

export { cn } from './lib/cn';
export { preset as tailwindPreset } from '../tailwind.preset';
