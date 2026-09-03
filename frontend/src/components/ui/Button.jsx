import React from 'react';

/**
 * Shared Button (redesign brief section 4). Four variants, one size scale. Every page must
 * consume this rather than a page-local button style.
 *
 * variant: 'primary' | 'secondary' | 'ghost' | 'danger'
 * size: 'compact' (32px) | 'default' (36px)
 */
export default function Button({
  variant = 'secondary',
  size = 'default',
  as: Component = 'button',
  className = '',
  children,
  ...rest
}) {
  const classes = ['ui-btn', `ui-btn-${variant}`, `ui-btn-${size}`, className].filter(Boolean).join(' ');
  return (
    <Component className={classes} type={Component === 'button' ? rest.type || 'button' : undefined} {...rest}>
      {children}
    </Component>
  );
}
