import assert from 'node:assert/strict';
import {readFile,readdir} from 'node:fs/promises';
import path from 'node:path';
const root=path.resolve(import.meta.dirname,'..'),pkg=JSON.parse(await readFile(path.join(root,'package.json'),'utf8'));
assert.equal(pkg.name,'com.breadpack.breadlingo.unity');assert.equal(pkg.license,'MIT');assert.equal(pkg.dependencies['com.unity.localization'],'1.4.5');
const guids=new Set();
async function inspect(directory){for(const e of await readdir(directory,{withFileTypes:true})){
 if(['.git','artifacts','node_modules'].includes(e.name))continue;
 assert(!/(?:^\.env(?:\.|$)|credentials\.json|token\.json|\.dll$|\.pdb$|\.prefab$|\.asset$)/i.test(e.name),`Private/binary asset forbidden: ${e.name}`);
 const file=path.join(directory,e.name);if(e.isDirectory()){await inspect(file);continue;}
 if(/\.(cs|asmdef|json|md|mjs|ya?ml|meta)$/.test(e.name)){const text=await readFile(file,'utf8');assert(!/(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|[A-Z]:[\\/]Projects[\\/])/i.test(text),`Private content in ${e.name}`);
 if(e.name.endsWith('.meta')){const guid=text.match(/^guid: ([a-f0-9]{32})$/m)?.[1];assert(guid&&!guids.has(guid),'Missing or duplicate Unity GUID');guids.add(guid);}}
 if(/\.(cs|asmdef)$/.test(e.name))await readFile(file+'.meta');
}}
const editor=JSON.parse(await readFile(path.join(root,'Editor/BreadTranslator.Unity.Editor.asmdef'),'utf8'));assert.deepEqual(editor.includePlatforms,['Editor']);assert(editor.references.includes('Unity.Localization.Editor'));
await inspect(root);console.log(`Package boundary and GUID checks passed: ${pkg.name}@${pkg.version}`);
