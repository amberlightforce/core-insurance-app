import {
  Banknote,
  FileText,
  Files,
  LayoutDashboard,
  MessageSquare,
  type LucideIcon,
} from 'lucide-react';

/**
 * The customer's navigation (BO-CHN-12): five areas, deliberately fewer than the staff rail. `to` is relative to
 * `/portal`. Areas whose pages have not landed (BO-CHN-13) route to the "coming soon" page; the page work package
 * replaces the stub route in `portalRoutes.tsx`, the nav entry stays.
 */
export interface PortalNavItem {
  id: 'overview' | 'policies' | 'billing' | 'documents' | 'messages';
  to: string;
  icon: LucideIcon;
}

export const portalNav: readonly PortalNavItem[] = [
  { id: 'overview', to: '/portal', icon: LayoutDashboard },
  { id: 'policies', to: '/portal/policies', icon: FileText },
  { id: 'billing', to: '/portal/billing', icon: Banknote },
  { id: 'documents', to: '/portal/documents', icon: Files },
  { id: 'messages', to: '/portal/messages', icon: MessageSquare },
];

/** Path of the public withdrawal declaration (reachable without sign-in, D-USR open decision #2). */
export const withdrawalPath = '/portal/withdrawal';
