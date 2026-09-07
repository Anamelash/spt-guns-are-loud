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
 const char* names[]={"Mic HP","Mic LP","Quiet gain","Threshold","Ratio","Knee","Attack","Hold","Release","Ceiling","Wet","Reset"};
 bool ok=n==1&&d&&abi&&instances&&frames&&abi()==1&&instances()==0&&d[0]->apiversion==0x010402&&std::strcmp(d[0]->name,"GAL Headphone Electronics")==0&&d[0]->numparameters==12;
 for(int i=0;ok&&i<12;++i)ok=std::strcmp(d[0]->paramdefs[i].name,names[i])==0&&d[0]->paramdefs[i].description;
 ok=ok&&d[0]->paramdefs[10].defaultval==0&&d[0]->process&&d[0]->create&&d[0]->reset&&d[0]->release;
 UnityAudioEffectState state{};state.structsize=sizeof(state);state.samplerate=48000;
 if(ok)ok=d[0]->create(&state)==UNITY_AUDIODSP_OK&&instances()==1;
 float input[512]{},output[512]{};for(float&x:input)x=1;
 if(ok){
  d[0]->setfloatparameter(&state,10,1);d[0]->setfloatparameter(&state,6,0);d[0]->process(&state,input,output,256,2,2);
  for(int fidx=0;fidx<256;++fidx)input[fidx*2]=input[fidx*2+1]=.01f*std::sin(6.2831853f*1000*fidx/48000);
  d[0]->process(&state,input,output,256,2,2);double staleEnergy=0;for(int i=256;i<512;++i)staleEnergy+=output[i]*output[i];
  d[0]->setfloatparameter(&state,10,0);d[0]->setfloatparameter(&state,11,1);d[0]->process(&state,input,output,256,2,2);bool exact=true;for(int i=0;i<512;++i)exact=exact&&output[i]==input[i];
  d[0]->setfloatparameter(&state,10,1);d[0]->process(&state,input,output,256,2,2);double recoveredEnergy=0;for(int i=256;i<512;++i)recoveredEnergy+=output[i]*output[i];
  bool recovered=recoveredEnergy>staleEnergy*1.4;if(!exact||!recovered)std::cerr<<" reset exact="<<exact<<" staleE="<<staleEnergy<<" recoveredE="<<recoveredEnergy<<'\n';ok=ok&&exact&&recovered&&frames()>=1024;d[0]->release(&state);ok=ok&&instances()==0;
 }
 std::cout<<(ok?"PASS":"FAIL")<<" Unity registration ABI and 12 parameters\n"; FreeLibrary(h); return ok?0:1;
}
