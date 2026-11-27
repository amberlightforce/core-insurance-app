import { Navigate, Outlet } from 'react-router';

import { useDevSession } from '../dev-auth/devAuth';
import { isPortalCustomerOnly } from './roles';

/**
 * Guards the staff shell against customers (BO-CHN-12): a signed-in user whose only role is `Customer.Portal` is sent
 * to the portal instead of seeing staff screens. Everyone else (including a signed-out visitor, who meets the usual
 * no-permission states) passes through. The API enforces its own permissions on every call.
 */
export function StaffGate({ children }: { children?: React.ReactNode }) {
  const roles = useDevSession()?.user.roles ?? [];
  if (isPortalCustomerOnly(roles)) return <Navigate to="/portal" replace />;
  return children ?? <Outlet />;
}
