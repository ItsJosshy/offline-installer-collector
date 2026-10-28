export interface Auth {authenticated:boolean;setupRequired:boolean;csrfToken:string;username?:string}
export interface Library {id:string;path:string;active:boolean;accessible:boolean;freeBytes:number|null}
export interface TrackedApp {id:string;name:string;description:string;publisher:string;paused:boolean;keepVersions:number;lastChecked:string|null;lastError:string|null}
export interface Source {id:string;appId:string;provider:string;identifier:string;channel:string;assetFilter:string;metadataUrl?:string;retrievedAt:string|null}
export interface Artifact {key:string;version:string;releaseId:string;os:string;architecture:string;format:string;url:string;fileName:string;sha256?:string;artifactType:string;requirements?:string}
export interface Variant extends Artifact {id:string;sourceId:string;appId:string;tracked:boolean;available:boolean;expectedSha256?:string}
export interface ArchiveFile {id:string;libraryId:string;appId:string|null;variantId:string|null;relativePath:string;fileName:string;size:number;modifiedAt:string;sha256:string|null;managed:boolean;missing:boolean;pinned:boolean;name:string;version:string;os:string;architecture:string;metadataOrigin:string;confidence:string;verification:string;artifactType:string;dependencyStatus:string;offlineTestNotes:string|null;offlineTestEnvironment:string|null;offlineTestDate:string|null;originalUrl?:string;resolvedUrl?:string;releaseId?:string;downloadedAt?:string}
export interface Job {id:string;kind:string;libraryId:string;libraryPath:string;appId:string|null;variantId:string|null;state:string;bytes:number;totalBytes:number|null;bytesPerSecond:number;attempts:number;error:string|null;cancelRequested:boolean;createdAt:string;finishedAt:string|null}
export interface Settings {concurrency:number;scheduleEnabled:boolean;checkHour:number;timezone:string}
export interface Capability {id?:string;name?:string;provider?:string;status:string;description?:string;detail?:string;message?:string}
export interface VaultState {apps:TrackedApp[];sources:Source[];variants:Variant[];files:ArchiveFile[];jobs:Job[];libraries:Library[];settings:Settings;capabilities:Capability[];configuration?:Record<string,unknown>}
export interface ProviderHit {identifier:string;name:string;description:string;url?:string}
export interface ProviderResult {name:string;description:string;publisher:string;artifacts:Artifact[];warning?:string}
export interface DirectoryResult {path:string|null;parent:string|null;roots:string[];directories:{name:string;path:string}[];error?:string}
export const bytes=(n:number|null|undefined)=>{if(n==null)return 'Unknown';if(n===0)return '0 B';const i=Math.min(Math.floor(Math.log(Math.max(n,1))/Math.log(1024)),4);return `${(n/1024**i).toLocaleString(undefined,{maximumFractionDigits:i?1:0})} ${['B','KB','MB','GB','TB'][i]}`};
export const date=(v:string|null|undefined)=>v?new Date(v).toLocaleString(undefined,{dateStyle:'medium',timeStyle:'short'}):'Never';
export const archivedFor=(id:string,files:ArchiveFile[])=>files.filter(f=>f.variantId===id&&!f.missing);
export const needsDownload=(v:Variant,files:ArchiveFile[])=>v.tracked&&v.available&&!archivedFor(v.id,files).some(f=>f.version===v.version);
export const activeJob=(j:Job)=>['queued','running','retrying'].includes(j.state);
export const osName=(os:string)=>({windows:'Windows',macos:'macOS',linux:'Linux',unknown:'Unknown OS'}[os]||os);
