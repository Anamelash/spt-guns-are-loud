#include "AudioPluginInterface.h"
#include "GalHeadphoneDsp.h"
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <new>

namespace {
enum Param { MicHP, MicLP, QuietGain, Threshold, Ratio, Knee, Attack, Hold, Release, Ceiling, Wet, ResetGeneration, ParamCount };
constexpr float Defaults[ParamCount]={120,11000,6,-24,10,6,.5f,10,150,.98f,0,0};
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
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK process(UnityAudioEffectState* s,float*in,float*out,unsigned len,int ic,int oc){
 auto*d=static_cast<EffectData*>(s->effectdata);if(!d)return UNITY_AUDIODSP_ERR_UNSUPPORTED;
 const unsigned requestedGeneration=generation(d->p[ResetGeneration].load());if(requestedGeneration!=d->resetGeneration){d->dsp.reset(static_cast<float>(s->samplerate),d->p[QuietGain].load());d->resetGeneration=requestedGeneration;}
 gal::Parameters p{d->p[0],d->p[1],d->p[2],d->p[3],d->p[4],d->p[5],d->p[6],d->p[7],d->p[8],d->p[9],d->p[10]};d->dsp.process(in,out,len,ic,oc,p);processedFrames.fetch_add(len,std::memory_order_relaxed);return UNITY_AUDIODSP_OK;}
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK setp(UnityAudioEffectState*s,int i,float v){if(i<0||i>=ParamCount)return UNITY_AUDIODSP_ERR_UNSUPPORTED;static_cast<EffectData*>(s->effectdata)->p[i].store(v);return UNITY_AUDIODSP_OK;}
UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK getp(UnityAudioEffectState*s,int i,float*v,char*str){if(i<0||i>=ParamCount||!v)return UNITY_AUDIODSP_ERR_UNSUPPORTED;*v=static_cast<EffectData*>(s->effectdata)->p[i].load();if(str)std::snprintf(str,16,"%.3g",*v);return UNITY_AUDIODSP_OK;}
UnityAudioParameterDefinition defs[ParamCount]{}; UnityAudioEffectDefinition effect{}; UnityAudioEffectDefinition* ptrs[]={&effect};
void def(int i,const char*n,const char*u,float lo,float hi,float dv){std::strncpy(defs[i].name,n,15);std::strncpy(defs[i].unit,u,15);defs[i].description=n;defs[i].min=lo;defs[i].max=hi;defs[i].defaultval=dv;defs[i].displayscale=1;defs[i].displayexponent=1;}
void init(){if(effect.structsize)return;def(MicHP,"Mic HP","Hz",10,1000,120);def(MicLP,"Mic LP","Hz",1000,22000,11000);def(QuietGain,"Quiet gain","dB",-24,24,6);def(Threshold,"Threshold","dBFS",-96,0,-24);def(Ratio,"Ratio",":1",1,100,10);def(Knee,"Knee","dB",0,48,6);def(Attack,"Attack","ms",0,1000,.5f);def(Hold,"Hold","ms",0,1000,10);def(Release,"Release","ms",0,5000,150);def(Ceiling,"Ceiling","linear",.01f,1,.98f);def(Wet,"Wet","%",0,1,0);def(ResetGeneration,"Reset","generation",0,16777215,0);effect.structsize=sizeof(effect);effect.paramstructsize=sizeof(defs[0]);effect.apiversion=0x010402;effect.pluginversion=0x00010001;std::strcpy(effect.name,"GAL Headphone Electronics");effect.numparameters=ParamCount;effect.channels=0;effect.create=create;effect.release=release;effect.reset=reset;effect.process=process;effect.paramdefs=defs;effect.setfloatparameter=setp;effect.getfloatparameter=getp;}
}
extern "C" UNITY_AUDIODSP_EXPORT_API int AUDIO_CALLING_CONVENTION UnityGetAudioEffectDefinitions(UnityAudioEffectDefinition*** p){init();*p=ptrs;return 1;}
extern "C" UNITY_AUDIODSP_EXPORT_API int AUDIO_CALLING_CONVENTION GAL_HeadphonesAbiVersion(){return 1;}
extern "C" UNITY_AUDIODSP_EXPORT_API int AUDIO_CALLING_CONVENTION GAL_HeadphonesInstanceCount(){return instanceCount.load(std::memory_order_relaxed);}
extern "C" UNITY_AUDIODSP_EXPORT_API unsigned long long AUDIO_CALLING_CONVENTION GAL_HeadphonesProcessedFrames(){return processedFrames.load(std::memory_order_relaxed);}
