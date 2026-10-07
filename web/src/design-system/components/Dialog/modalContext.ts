import { createContext, use } from 'react';

/**
 * Number of modal layers (dialogs, modal drawers, the palette) around a component. Popovers and menus
 * inside a modal use the solid popover material, because glass is never stacked on glass (Part 1 §2.6).
 */
export const ModalDepthContext = createContext(0);

export function useModalDepth(): number {
  return use(ModalDepthContext);
}

/** The guide allows at most two stacked modals (Part 2 §4.18). */
export const maxStackedModals = 2;
