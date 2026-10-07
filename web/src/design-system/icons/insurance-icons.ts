import {
  BookOpen,
  Boxes,
  Car,
  ChartColumn,
  ClipboardList,
  Clock,
  Coins,
  Euro,
  FilePlus,
  Files,
  FileText,
  FolderOpen,
  House,
  Inbox,
  Layers,
  Link2,
  Megaphone,
  RectangleHorizontal,
  Scale,
  Settings,
  Shield,
  ShieldCheck,
  ShieldHalf,
  Stamp,
  Undo2,
  UserCheck,
  Users,
  Wallet,
  type LucideIcon,
} from 'lucide-react';

/**
 * The 18 custom insurance icons of Part 3 §7.1.1. D-FE-09: the artwork is not drawn yet, so each name maps
 * to the closest Lucide glyph as a known placeholder. Replace the mapping with the drawn components
 * (same names) when design delivers them; call sites do not change.
 */
export const insuranceIcons = {
  policy: Shield,
  'policy-term': FileText,
  endorsement: FilePlus,
  quote: Euro,
  bind: Link2,
  claim: FolderOpen,
  fnol: Megaphone,
  exposure: ShieldHalf,
  reserve: Coins,
  recovery: Undo2,
  'friendly-settlement': Car,
  'joint-report': ClipboardList,
  treaty: Layers,
  cession: ShieldCheck,
  mark: Stamp,
  'clock-statutory': Clock,
  'greek-plate': RectangleHorizontal,
  'maker-checker': UserCheck,
} as const satisfies Record<string, LucideIcon>;

export type InsuranceIconName = keyof typeof insuranceIcons;

/** Fixed module icons (Part 3 §7.2). */
export const moduleIcons = {
  home: House,
  work: Inbox,
  parties: Users,
  policies: insuranceIcons.policy,
  underwriting: Scale,
  claims: insuranceIcons.claim,
  billing: Wallet,
  reinsurance: insuranceIcons.treaty,
  finance: BookOpen,
  documents: Files,
  compliance: ShieldCheck,
  reports: ChartColumn,
  products: Boxes,
  admin: Settings,
} as const satisfies Record<string, LucideIcon>;

export type ModuleIconName = keyof typeof moduleIcons;
