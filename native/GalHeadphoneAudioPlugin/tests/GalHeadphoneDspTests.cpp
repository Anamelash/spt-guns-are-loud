#include "GalHeadphoneDsp.h"
#include <cmath>
#include <iostream>
#include <limits>
#include <vector>
static int fails=0; static void check(bool x,const char*m){if(!x){std::cerr<<"FAIL "<<m<<'\n';++fails;}}
int main(){
 gal::Parameters p; gal::Processor d; d.reset(48000); std::vector<float> in(4096),out(4096);
 for(size_t i=0;i<in.size();++i) in[i]=float(std::sin(i*.031)*.2);
 d.process(in.data(),out.data(),2048,2,2,p);check(in==out,"wet zero exact bypass");
 p.wet=1; p.micHighpassHz=20;p.micLowpassHz=20000;p.ceiling=.3f;std::fill(in.begin(),in.end(),1);d.reset(48000);d.process(in.data(),out.data(),2048,2,2,p);for(float x:out)check(std::isfinite(x)&&std::abs(x)<=.30001f,"finite ceiling");
 d.reset(48000);std::fill(in.begin(),in.end(),.01f);for(int i=0;i<200;i+=2)in[i]=1;d.process(in.data(),out.data(),2048,2,2,p);check(d.gainReductionDb()>1,"linked detector compresses");
 in[0]=std::numeric_limits<float>::quiet_NaN();in[1]=std::numeric_limits<float>::infinity();d.process(in.data(),out.data(),2048,2,2,p);for(float x:out)check(std::isfinite(x),"nonfinite recovery");
 gal::Processor a,b;a.reset(48000);b.reset(48000);std::vector<float> oa(4096),ob(4096);std::fill(in.begin(),in.end(),.2f);a.process(in.data(),oa.data(),2048,2,2,p);for(int f=0;f<2048;f+=64)b.process(in.data()+f*2,ob.data()+f*2,64,2,2,p);float err=0;for(size_t i=0;i<oa.size();++i)err=std::max(err,std::abs(oa[i]-ob[i]));check(err<1e-6f,"block invariant");
 p.micHighpassHz=500;p.micLowpassHz=12000;p.thresholdDbFs=-30;p.attackMs=0;p.holdMs=0;p.releaseMs=20;p.ceiling=1;
 std::vector<float> low(96000),mid(96000),sink(96000);for(int f=0;f<48000;++f){float l=.2f*std::sin(6.2831853f*20*f/48000);float m=.2f*std::sin(6.2831853f*1000*f/48000);low[f*2]=low[f*2+1]=l;mid[f*2]=mid[f*2+1]=m;}
 gal::Processor lowD,midD;lowD.reset(48000);midD.reset(48000);lowD.process(low.data(),sink.data(),48000,2,2,p);midD.process(mid.data(),sink.data(),48000,2,2,p);check(midD.gainReductionDb()>lowD.gainReductionDb()+6,"mic highpass precedes detector");
 std::cout<<(fails?"FAILED ":"PASS ")<<"native DSP invariants"<<'\n';return fails?1:0;
}
