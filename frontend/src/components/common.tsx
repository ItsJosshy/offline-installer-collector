import {Archive,LoaderCircle,TriangleAlert} from 'lucide-react';
import type {ReactNode} from 'react';
import {osName} from '../domain';
export function Badge({children,tone='neutral'}:{children:ReactNode;tone?:'neutral'|'success'|'warning'|'danger'|'accent'}){return <span className={`badge badge-${tone}`}>{children}</span>}
export function OS({value}:{value:string}){return <Badge>{osName(value)}</Badge>}
export function Empty({title,children,action}:{title:string;children:ReactNode;action?:ReactNode}){return <div className="empty"><span className="empty-icon"><Archive size={28}/></span><h3>{title}</h3><p>{children}</p>{action}</div>}
export function Loading(){return <div className="loading" role="status"><LoaderCircle className="spin" size={22}/> Loading your vault…</div>}
export function ErrorBox({children}:{children:ReactNode}){return <div className="alert alert-danger" role="alert"><TriangleAlert size={18}/><span>{children}</span></div>}
export function Field({label,children,hint}:{label:string;children:ReactNode;hint?:string}){return <label className="field"><span>{label}</span>{children}{hint&&<small>{hint}</small>}</label>}
export type Mutate=(path:string,method?:string,body?:unknown,message?:string)=>Promise<void>;
