import type { ReactNode } from 'react';
import { useNavigate } from 'react-router';

import { Button, type ButtonVariant } from '../../design-system';

export interface LinkButtonProps {
  to: string;
  children: ReactNode;
  variant?: ButtonVariant;
}

/** In-app navigation as a design-system link-style button (the design system has no router-bound link). */
export function LinkButton({ to, children, variant = 'link' }: LinkButtonProps) {
  const navigate = useNavigate();
  return (
    <Button
      variant={variant}
      onPress={() => {
        void navigate(to);
      }}
    >
      {children}
    </Button>
  );
}
