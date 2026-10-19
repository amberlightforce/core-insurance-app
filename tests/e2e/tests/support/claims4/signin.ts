import type { APIRequestContext, Page } from '@playwright/test';
import { signIn } from '../api.js';
import { signInAs } from '../ui.js';
import { SL4_USERS, type Sl4User } from './users.js';

/** API token of a slice 4 dev user. */
export const tokenFor = (request: APIRequestContext, user: Sl4User): Promise<string> => signIn(request, SL4_USERS[user].id);

/** Browser dev sign-in by the exact display name of a slice 4 dev user. */
export const uiSignInAs = (page: Page, user: Sl4User): Promise<void> => signInAs(page, SL4_USERS[user].displayName);
