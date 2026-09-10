import { useEffect, type RefObject } from 'react';

/**
 * Closes a popover when focus or a click leaves it, or on Escape.
 *
 * Both dropdowns in the top bar need identical behaviour, and getting it
 * slightly different in each is how a menu ends up staying open behind a modal.
 * Escape is not optional — a menu with no keyboard exit fails WCAG 2.1.1.
 */
export function useDismissable(
  ref: RefObject<HTMLElement | null>,
  open: boolean,
  onDismiss: () => void,
): void {
  useEffect(() => {
    if (!open) return;

    function handlePointerDown(event: MouseEvent | TouchEvent) {
      const target = event.target;
      if (target instanceof Node && !ref.current?.contains(target)) {
        onDismiss();
      }
    }

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onDismiss();
    }

    document.addEventListener('mousedown', handlePointerDown);
    document.addEventListener('touchstart', handlePointerDown);
    document.addEventListener('keydown', handleKeyDown);

    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('touchstart', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [ref, open, onDismiss]);
}
