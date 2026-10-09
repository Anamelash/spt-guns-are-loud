#include "AudioPluginInterface.h"
#include "GalHeadphoneDsp.h"
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <new>

namespace {
// Indices 0-11 are ABI 1, 12-18 ABI 2, 19 ABI 3; none may move: compiled mixers address parameters by slot.
enum Param { MicHP, MicLP, QuietGain, Threshold, Ratio, Knee, Attack, Hold, Release, Ceiling, Wet, ResetGeneration,
             LowShelf, LowShelfFreq, Presence, PresenceFreq, Noise, Saturation, Delay, BandOrder, ParamCount };
constexpr float Defaults[ParamCount]={120,11000,6,-24,10,6,.5f,10,150,.98f,0,0,0,200,0,3200,-120,0,0,1};
struct EffectData { gal::Processor dsp; std::array<std::atomic<float>,ParamCount> p; unsigned resetGeneration=0; };
std::atomic<int> instanceCount{0};
std::atomic<unsigned long long> processedFrames{0};
unsigned generation(float value){return std::isfinite(value)&&value>0?static_cast<unsigned>(value>16777215?16777215:value):0;}
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK create(UnityAudioEffectState* s) {
    auto* d=new(std::nothrow) EffectData(); if(!d) return UNITY_AUDIODSP_ERR_UNSUPPORTED;
    for(int i=0;i<ParamCount;++i) d->p[i].store(Defaults[i]);
    d->dsp.reset(static_cast<float>(s->samplerate)); s->effectdata=d; instanceCount.fetch_add(1,std::memory_order_relaxed); return UNITY_AUDIODSP_OK;
}
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK release(UnityAudioEffectState* s){if(s->effectdata){delete static_cast<EffectData*>(s->effectdata);s->effectdata=nullptr;instanceCount.fetch_sub(1,std::memory_order_relaxed);}return UNITY_AUDIODSP_OK;}
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK reset(UnityAudioEffectState* s){auto*d=static_cast<EffectData*>(s->effectdata);if(d){d->dsp.reset(static_cast<float>(s->samplerate),d->p[QuietGain].load());d->resetGeneration=generation(d->p[ResetGeneration].load());}return UNITY_AUDIODSP_OK;}
gal::Parameters snapshot(const EffectData& d){
 gal::Parameters p;
 p.micHighpassHz=d.p[MicHP]; p.micLowpassHz=d.p[MicLP]; p.quietGainDb=d.p[QuietGain]; p.thresholdDbFs=d.p[Threshold];
 p.ratio=d.p[Ratio]; p.kneeDb=d.p[Knee]; p.attackMs=d.p[Attack]; p.holdMs=d.p[Hold]; p.releaseMs=d.p[Release];
 p.ceiling=d.p[Ceiling]; p.wet=d.p[Wet]; p.lowShelfDb=d.p[LowShelf]; p.lowShelfHz=d.p[LowShelfFreq];
 p.presenceDb=d.p[Presence]; p.presenceHz=d.p[PresenceFreq]; p.noiseDbFs=d.p[Noise]; p.saturation=d.p[Saturation];
 p.delayMs=d.p[Delay]; p.bandOrder=d.p[BandOrder]; return p;
}
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK process(UnityAudioEffectState* s,float*in,float*out,unsigned len,int ic,int oc){
 auto*d=static_cast<EffectData*>(s->effectdata);if(!d)return UNITY_AUDIODSP_ERR_UNSUPPORTED;
 const unsigned requestedGeneration=generation(d->p[ResetGeneration].load());if(requestedGeneration!=d->resetGeneration){d->dsp.reset(static_cast<float>(s->samplerate),d->p[QuietGain].load());d->resetGeneration=requestedGeneration;}
 const gal::Parameters p=snapshot(*d);d->dsp.process(in,out,len,ic,oc,p);processedFrames.fetch_add(len,std::memory_order_relaxed);return UNITY_AUDIODSP_OK;}
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK setp(UnityAudioEffectState*s,int i,float v){if(i<0||i>=ParamCount)return UNITY_AUDIODSP_ERR_UNSUPPORTED;static_cast<EffectData*>(s->effectdata)->p[i].store(v);return UNITY_AUDIODSP_OK;}
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK getp(UnityAudioEffectState*s,int i,float*v,char*str){if(i<0||i>=ParamCount||!v)return UNITY_AUDIODSP_ERR_UNSUPPORTED;*v=static_cast<EffectData*>(s->effectdata)->p[i].load();if(str)std::snprintf(str,16,"%.3g",*v);return UNITY_AUDIODSP_OK;}
UnityAudioParameterDefinition defs[ParamCount]{}; UnityAudioEffectDefinition effect{}; UnityAudioEffectDefinition* ptrs[]={&effect};
void def(int i,const char*n,const char*u,float lo,float hi,float dv){std::strncpy(defs[i].name,n,15);std::strncpy(defs[i].unit,u,15);defs[i].description=n;defs[i].min=lo;defs[i].max=hi;defs[i].defaultval=dv;defs[i].displayscale=1;defs[i].displayexponent=1;}
void init(){if(effect.structsize)return;def(MicHP,"Mic HP","Hz",10,1000,120);def(MicLP,"Mic LP","Hz",1000,22000,11000);def(QuietGain,"Quiet gain","dB",-24,24,6);def(Threshold,"Threshold","dBFS",-96,0,-24);def(Ratio,"Ratio",":1",1,100,10);def(Knee,"Knee","dB",0,48,6);def(Attack,"Attack","ms",0,1000,.5f);def(Hold,"Hold","ms",0,1000,10);def(Release,"Release","ms",0,5000,150);def(Ceiling,"Ceiling","linear",.01f,1,.98f);def(Wet,"Wet","%",0,1,0);def(ResetGeneration,"Reset","generation",0,16777215,0);
 def(LowShelf,"Low shelf","dB",-12,0,0);def(LowShelfFreq,"Low shelf freq","Hz",60,500,200);def(Presence,"Presence","dB",0,12,0);def(PresenceFreq,"Presence freq","Hz",1000,6000,3200);def(Noise,"Noise","dBFS",-120,-30,-120);def(Saturation,"Saturation","fraction",0,1,0);def(Delay,"Delay","ms",0,8,0);def(BandOrder,"Band order","order",1,2,1);
 effect.structsize=sizeof(effect);effect.paramstructsize=sizeof(defs[0]);effect.apiversion=0x010402;effect.pluginversion=0x00010003;std::strcpy(effect.name,"GAL Headphone Electronics");effect.numparameters=ParamCount;effect.channels=0;effect.create=create;effect.release=release;effect.reset=reset;effect.process=process;effect.paramdefs=defs;effect.setfloatparameter=setp;effect.getfloatparameter=getp;}
}
extern "C" UNITY_AUDIODSP_EXPORT_API int AUDIO_CALLING_CONVENTION UnityGetAudioEffectDefinitions(UnityAudioEffectDefinition*** p){init();*p=ptrs;return 1;}
extern "C" UNITY_AUDIODSP_EXPORT_API int AUDIO_CALLING_CONVENTION GAL_HeadphonesAbiVersion(){return 3;}
extern "C" UNITY_AUDIODSP_EXPORT_API int AUDIO_CALLING_CONVENTION GAL_HeadphonesInstanceCount(){return instanceCount.load(std::memory_order_relaxed);}
extern "C" UNITY_AUDIODSP_EXPORT_API unsigned long long AUDIO_CALLING_CONVENTION GAL_HeadphonesProcessedFrames(){return processedFrames.load(std::memory_order_relaxed);}
