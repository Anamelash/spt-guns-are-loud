#include "AudioPluginInterface.h"
#include <windows.h>
#include <cmath>
#include <cstring>
#include <iostream>
int main(int argc,char**argv){
 if(argc!=2)return 2;
 HMODULE h=LoadLibraryA(argv[1]); if(!h){std::cerr<<"LoadLibrary failed\n";return 1;}
 FARPROC symbol=GetProcAddress(h,"UnityGetAudioEffectDefinitions");
 using Entry=int(AUDIO_CALLING_CONVENTION*)(UnityAudioEffectDefinition***); Entry f=nullptr;
 static_assert(sizeof(f)==sizeof(symbol)); std::memcpy(&f,&symbol,sizeof(f)); if(!f)return 1;
 UnityAudioEffectDefinition**d=nullptr; int n=f(&d);
 using IntDiagnostic=int(AUDIO_CALLING_CONVENTION*)();using FramesDiagnostic=unsigned long long(AUDIO_CALLING_CONVENTION*)();
 FARPROC abiSymbol=GetProcAddress(h,"GAL_HeadphonesAbiVersion"),instancesSymbol=GetProcAddress(h,"GAL_HeadphonesInstanceCount"),framesSymbol=GetProcAddress(h,"GAL_HeadphonesProcessedFrames");IntDiagnostic abi=nullptr,instances=nullptr;FramesDiagnostic frames=nullptr;std::memcpy(&abi,&abiSymbol,sizeof(abi));std::memcpy(&instances,&instancesSymbol,sizeof(instances));std::memcpy(&frames,&framesSymbol,sizeof(frames));
 // ABI 1 slots 0-11 keep their indices; ABI 2 appends seven character controls, ABI 3 the band order.
 struct Expected{const char*name;float min,max,defaultval;};
 const Expected expected[]={{"Mic HP",10,1000,120},{"Mic LP",1000,22000,11000},{"Quiet gain",-24,24,6},{"Threshold",-96,0,-24},{"Ratio",1,100,10},{"Knee",0,48,6},{"Attack",0,1000,.5f},{"Hold",0,1000,10},{"Release",0,5000,150},{"Ceiling",.01f,1,.98f},{"Wet",0,1,0},{"Reset",0,16777215,0},
  {"Low shelf",-12,0,0},{"Low shelf freq",60,500,200},{"Presence",0,12,0},{"Presence freq",1000,6000,3200},{"Noise",-120,-30,-120},{"Saturation",0,1,0},{"Delay",0,8,0},{"Band order",1,2,1}};
 constexpr int Count=sizeof(expected)/sizeof(expected[0]);
 bool ok=n==1&&d&&abi&&instances&&frames&&abi()==3&&instances()==0&&d[0]->apiversion==0x010402&&d[0]->pluginversion==0x00010003&&std::strcmp(d[0]->name,"GAL Headphone Electronics")==0&&d[0]->numparameters==Count;
 for(int i=0;ok&&i<Count;++i){const auto&p=d[0]->paramdefs[i];ok=std::strcmp(p.name,expected[i].name)==0&&p.description&&p.min==expected[i].min&&p.max==expected[i].max&&p.defaultval==expected[i].defaultval;if(!ok)std::cerr<<" parameter "<<i<<" "<<p.name<<'\n';}
 ok=ok&&d[0]->process&&d[0]->create&&d[0]->reset&&d[0]->release;
 UnityAudioEffectState state{};state.structsize=sizeof(state);state.samplerate=48000;
 if(ok)ok=d[0]->create(&state)==UNITY_AUDIODSP_OK&&instances()==1;
 for(int i=0;ok&&i<Count;++i){float v=-1;ok=d[0]->getfloatparameter(&state,i,&v,nullptr)==UNITY_AUDIODSP_OK&&v==expected[i].defaultval;}
 ok=ok&&d[0]->setfloatparameter(&state,Count,1)!=UNITY_AUDIODSP_OK;
 float input[512]{},output[512]{};for(float&x:input)x=1;
 if(ok){
  d[0]->setfloatparameter(&state,10,1);d[0]->setfloatparameter(&state,6,0);d[0]->process(&state,input,output,256,2,2);
  for(int fidx=0;fidx<256;++fidx)input[fidx*2]=input[fidx*2+1]=.01f*std::sin(6.2831853f*1000*fidx/48000);
  d[0]->process(&state,input,output,256,2,2);double staleEnergy=0;for(int i=256;i<512;++i)staleEnergy+=output[i]*output[i];
  d[0]->setfloatparameter(&state,10,0);d[0]->setfloatparameter(&state,11,1);d[0]->process(&state,input,output,256,2,2);bool exact=true;for(int i=0;i<512;++i)exact=exact&&output[i]==input[i];
  d[0]->setfloatparameter(&state,10,1);d[0]->process(&state,input,output,256,2,2);double recoveredEnergy=0;for(int i=256;i<512;++i)recoveredEnergy+=output[i]*output[i];
  bool recovered=recoveredEnergy>staleEnergy*1.4;if(!exact||!recovered)std::cerr<<" reset exact="<<exact<<" staleE="<<staleEnergy<<" recoveredE="<<recoveredEnergy<<'\n';ok=ok&&exact&&recovered&&frames()>=1024;
  // Slot 16 reaches the processor: with silent input the noise control alone makes output.
  float silent[512]{};d[0]->setfloatparameter(&state,16,-60);d[0]->process(&state,silent,output,256,2,2);double noise=0;for(float x:output)noise+=x*x;
  d[0]->setfloatparameter(&state,16,-120);d[0]->setfloatparameter(&state,11,2);d[0]->process(&state,silent,output,256,2,2);double quiet=0;for(float x:output)quiet+=x*x;
  if(!(noise>0&&quiet==0)){std::cerr<<" noise slot energy="<<noise<<" off="<<quiet<<'\n';}
  ok=ok&&noise>0&&quiet==0;
  d[0]->release(&state);ok=ok&&instances()==0;
 }
 std::cout<<(ok?"PASS":"FAIL")<<" Unity registration ABI 3 and 20 parameters\n"; FreeLibrary(h); return ok?0:1;
}
