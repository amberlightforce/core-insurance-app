import { createRequire } from 'node:module';
import fs from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const require = createRequire(join(root, 'tests/e2e/package.json'));
const { chromium } = require('@playwright/test');
const out = dirname(fileURLToPath(import.meta.url));
fs.mkdirSync(out, { recursive: true });
const stub = `(() => {
 sessionStorage.setItem('coreins.devSession',JSON.stringify({accessToken:'fixture',expiresAt:'2099-01-01T00:00:00Z',user:{id:'designauth',actorKey:'USER:dev:designauth',name:'Synthetic staff',roles:['Platform.DesignAuthority','Platform.ReleaseManager','Staff.UnderwritingManager','Staff.ClaimsManager']}}));
 const orig=fetch; window.fetch=async(input,init)=>{
 const url=new URL(typeof input==='string'?input:input instanceof URL?input.href:input.url,location.origin);
 if(!url.pathname.startsWith('/api/'))return orig(input,init);
 const p=await import('/src/modules/market/packs/fixtures.ts');const e=await import('/src/modules/policy/packRollback/fixtures.ts');
 let body={items:[],nextCursor:null};
 if(url.pathname==='/api/mkt/v1/packs') body={items:[p.pack,{packId:'018f8000-0000-7000-8000-000000000084',pack:'CY-STUB',scope:'COUNTRY',versions:[],activeVersions:[]}],nextCursor:null};
 else if(url.pathname.startsWith('/api/mkt/v1/packs/') && init?.method!=='POST') body=p.pack;
 else if(url.pathname.startsWith('/api/mkt/v1/pack-activations/')) body=p.pendingActivation;
 else if(url.pathname==='/api/mkt/v1/packs/rollback'){const request=JSON.parse(init.body);body={activationId:null,preview:{...p.preview,fromVersion:p.pack.activeVersions[0].version,toVersion:request.toVersion,window:{from:p.pack.activeVersions[0].activeSince,to:'2026-10-09T12:00:00Z'},hashesIssued:[p.pack.activeVersions[0].configurationHash,'d'.repeat(64)]}};}
 else if(url.pathname==='/api/pol/v1/pack-rollback-exceptions')body={items:[e.exception],nextCursor:null};
 else if(url.pathname.startsWith('/api/pol/v1/pack-rollback-exceptions/'))body=e.exception;
 else if(url.pathname==='/api/plt/v1/approval')body={items:[{request:{requestId:p.activationId,type:'CLM.PAYMENT',objectRef:{module:'CLM',type:'Claim',id:e.exception.policyId},payloadHash:p.issuedHash,status:'PendingApproval',maker:{kind:'USER',id:'synthetic-claims'},authority:{type:'CLM.PAYMENT',amount:{amount:'5500.00',currency:'EUR'}},referralRole:'Staff.ClaimsManager',reason:'Συνθετικό αίτημα αποζημίωσης',diff:{},requestedAt:'2026-10-09T09:00:00Z',version:1}}],nextCursor:null};
 return new Response(JSON.stringify(body),{status:200,headers:{'Content-Type':'application/json'}});
 };})();`;
const browser = await chromium.launch();
try {
 for (const [variant,width,scheme] of [['light',1440,'light'],['dark',1440,'dark'],['840',840,'light']]) {
  for (const [name,path,dialog] of [
   ['reference-claims-inbox','/claims/approvals'],['registry','/admin/packs'],
   ['pack-detail','/admin/packs/018f8000-0000-7000-8000-000000000081'],
   ['rollback-preview','/admin/packs/018f8000-0000-7000-8000-000000000081','rollback'],
   ['checker','/admin/packs/018f8000-0000-7000-8000-000000000081/activations/018f8000-0000-7000-8000-000000000082'],
   ['exception-queue','/policies/pack-rollback'],
   ['exception-detail','/policies/pack-rollback/018f8000-0000-7000-8000-000000000091'],
   ['exception-review','/policies/pack-rollback/018f8000-0000-7000-8000-000000000091','review']
  ]) {
   if(process.env.PACK_VISUAL_FILTER && !process.env.PACK_VISUAL_FILTER.split(',').includes(name))continue;
   const context=await browser.newContext({viewport:{width,height:1100},colorScheme:scheme,locale:'el-GR'});
   const page=await context.newPage(); const errors=[];page.on('pageerror',e=>errors.push(String(e)));
   await page.addInitScript(stub);await page.goto('http://127.0.0.1:5397'+path);
   await page.getByRole('heading',{level:1}).waitFor();await page.waitForTimeout(1200);
   const label=async(key,ns='market')=>page.evaluate(async([key,ns])=>{const {default:i18n}=await import('/src/i18n/index.ts');return i18n.t(key,{ns});},[key,ns]);
   if(dialog==='rollback'){
    await page.getByRole('button',{name:await label('packs.detail.rollback'),exact:true}).click();
    await page.getByRole('button',{name:new RegExp(await label('packs.dialog.rollbackTo'))}).click();
    await page.getByRole('option',{name:/0.2.0/}).click();
    await page.getByRole('textbox',{name:new RegExp(await label('packs.dialog.reason'))}).fill('Συνθετικός λόγος ελέγχου επαναφοράς');
    await page.getByRole('button',{name:await label('packs.dialog.preview'),exact:true}).click();
    await page.getByText('tax.treatment.rule.synthetic_8',{exact:true}).waitFor();
   } else if(dialog==='review'){
    await page.getByRole('button',{name:await label('packRollback.review.open','policy'),exact:true}).click();
    await page.getByRole('button',{name:new RegExp(await label('packRollback.review.outcome','policy'))}).click();
    await page.getByRole('option',{name:await label('packRollback.outcome.CORRECTION_REQUIRED','policy'),exact:true}).click();
    await page.getByRole('textbox',{name:new RegExp(await label('packRollback.review.reason','policy'))}).fill('Συνθετική εξέταση για χειροκίνητη διόρθωση');
   }
   await page.screenshot({path:out+'/'+name+'-'+variant+'.png',fullPage:true});
   if(dialog==='rollback'){
    await page.getByText('tax.treatment.rule.synthetic_8',{exact:true}).scrollIntoViewIfNeeded();
    await page.screenshot({path:out+'/'+name+'-bottom-'+variant+'.png',fullPage:true});
   }else if(name==='pack-detail'){
    await page.locator('main').evaluate(el=>{el.scrollTop=el.scrollHeight;});
    await page.screenshot({path:out+'/'+name+'-bottom-'+variant+'.png',fullPage:true});
   }
   const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth);
   if(errors.length||overflow)throw Error(JSON.stringify({name,variant,errors,overflow}));
   console.log(name+' '+variant+': rendered, no page errors/document overflow');await context.close();
  }
 }
} finally { await browser.close(); }
