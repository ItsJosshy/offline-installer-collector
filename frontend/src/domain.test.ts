import {describe,it,expect} from 'vitest';
import {archivedFor,needsDownload,bytes} from './domain';
describe('archive state',()=>{
 it('never calls a missing file archived',()=>expect(archivedFor('v',[{variantId:'v',missing:true}] as never)).toHaveLength(0));
 it('requires a present matching version for selected variants',()=>expect(needsDownload({id:'v',tracked:true,available:true,version:'2'} as never,[{variantId:'v',version:'1',missing:false}] as never)).toBe(true));
 it('does not report updates for untracked or unavailable variants',()=>{expect(needsDownload({tracked:false,available:true} as never,[])).toBe(false);expect(needsDownload({tracked:true,available:false} as never,[])).toBe(false)});
 it('formats zero and unknown sizes honestly',()=>{expect(bytes(0)).toBe('0 B');expect(bytes(null)).toBe('Unknown')});
});
