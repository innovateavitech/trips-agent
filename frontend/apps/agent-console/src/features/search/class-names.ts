/** Joins class names, dropping the falsy ones: `cx('base', isActive && 'bg-accent')`. */
export function cx(...names: Array<string | false | null | undefined>): string {
  return names.filter(Boolean).join(' ');
}
