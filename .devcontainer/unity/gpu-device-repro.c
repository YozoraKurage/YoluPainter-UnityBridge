/* 小さな GLX の compute と、同じプロセスの論理 D3D12 デバイスの RemoveDevice。
 * 物理 GPU のリセット・不正なシェーダー・メモリの使い尽くしは行わない。
 * COM の ABI/GUID: Microsoft DirectX-Headers の d3d12.h / dxcore_interface.h。 */
#define GL_GLEXT_PROTOTYPES
#include <GL/gl.h>
#include <GL/glx.h>
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

typedef struct { uint32_t a; uint16_t b, c; unsigned char d[8]; } guid;
static const guid factory_id = {0x78ee5945,0xc36e,0x4b13,{0xa6,0x69,0,0x5d,0xd1,0x1c,0x0f,0x06}};
static const guid list_id = {0x526c7776,0x40e9,0x459b,{0xb7,0x11,0xf3,0x2a,0xd7,0x6d,0xfc,0x28}};
static const guid adapter_id = {0xf0db4c7f,0xfe5a,0x42a2,{0xbd,0x62,0xf2,0xa6,0xcf,0x6f,0xc8,0x3e}};
static const guid graphics_id = {0x0c9ece4d,0x2f6e,0x4f01,{0x8c,0x96,0xe8,0x9e,0x33,0x1b,0x47,0xb1}};
static const guid device5_id = {0x8b4f173b,0x2fea,0x4b80,{0x8f,0x58,0x43,0x07,0x19,0x1a,0xb9,0x5d}};
static void *slot(void *object, unsigned index) { return (*(void ***)object)[index]; }
static void release(void *object) { if (object) ((uint32_t (*)(void *))slot(object,2))(object); }

static int remove_own_device(void)
{
    void *dxcore = dlopen("libdxcore.so", RTLD_NOW), *d3d12 = dlopen("libd3d12.so", RTLD_NOW);
    if (!dxcore || !d3d12) { fprintf(stderr,"WSL の D3D12 を読み込めない\n"); return 2; }
    int32_t (*create_factory)(const guid *,void **) = dlsym(dxcore,"DXCoreCreateAdapterFactory");
    int32_t (*create_device)(void *,uint32_t,const guid *,void **) = dlsym(d3d12,"D3D12CreateDevice");
    void *factory = NULL, *list = NULL, *adapter = NULL, *device = NULL;
    if (!create_factory || !create_device) return 2;
    int32_t hr = create_factory(&factory_id,&factory);
    if (hr >= 0) hr = ((int32_t (*)(void *,uint32_t,const guid *,const guid *,void **))slot(factory,3))(factory,1,&graphics_id,&list_id,&list);
    if (hr >= 0) {
        uint32_t count = ((uint32_t (*)(void *))slot(list,4))(list);
        if (count != 1) { fprintf(stderr,"安全な照合のため単一アダプターが必要（%u 台）\n",count); hr = -1; }
    }
    if (hr >= 0) hr = ((int32_t (*)(void *,uint32_t,const guid *,void **))slot(list,3))(list,0,&adapter_id,&adapter);
    if (hr >= 0) hr = create_device(adapter,0xb000,&device5_id,&device);
    fprintf(stderr,"REPRO Device5 hr=0x%08x\n",(uint32_t)hr);
    if (device) {
        /* Device5::RemoveDevice はスロット 58。ラッパーが singleton を指定し、Mesa と同じプロセス・アダプターの単一デバイス。
         * 実機全体をリセットする TDR の強制とは別。*/
        ((void (*)(void *))slot(device,58))(device);
        hr = ((int32_t (*)(void *))slot(device,37))(device);
        fprintf(stderr,"REPRO own-device removed reason=0x%08x\n",(uint32_t)hr);
    }
    release(device); release(adapter); release(list); release(factory);
    return device && hr < 0 ? 0 : 2;
}

int main(int argc, char **argv)
{
    int remove = argc == 2 && strcmp(argv[1],"--remove") == 0;
    if (argc > 1 && !remove) return 2;
    XInitThreads();
    Display *display = XOpenDisplay(NULL);
    if (!display) return 2;
    int attrs[] = {GLX_X_RENDERABLE,True,GLX_DRAWABLE_TYPE,GLX_WINDOW_BIT,GLX_RENDER_TYPE,GLX_RGBA_BIT,None};
    int count = 0;
    GLXFBConfig *configs = glXChooseFBConfig(display,DefaultScreen(display),attrs,&count);
    if (!configs || !count) return 2;
    XVisualInfo *visual = glXGetVisualFromFBConfig(display,configs[0]);
    if (!visual) return 2;
    XSetWindowAttributes wa = {.colormap=XCreateColormap(display,RootWindow(display,visual->screen),visual->visual,AllocNone)};
    Window window = XCreateWindow(display,RootWindow(display,visual->screen),0,0,16,16,0,visual->depth,InputOutput,visual->visual,CWColormap,&wa);
    PFNGLXCREATECONTEXTATTRIBSARBPROC create = (PFNGLXCREATECONTEXTATTRIBSARBPROC)glXGetProcAddressARB((const GLubyte *)"glXCreateContextAttribsARB");
    int ctxattrs[] = {GLX_CONTEXT_MAJOR_VERSION_ARB,4,GLX_CONTEXT_MINOR_VERSION_ARB,3,
        GLX_CONTEXT_RESET_NOTIFICATION_STRATEGY_ARB,GLX_LOSE_CONTEXT_ON_RESET_ARB,None};
    GLXContext context = create ? create(display,configs[0],NULL,True,ctxattrs) : NULL;
    if (!context || !glXMakeCurrent(display,window,context)) return 2;
    fprintf(stderr,"REPRO renderer=%s version=%s pid=%d\n",glGetString(GL_RENDERER),glGetString(GL_VERSION),getpid());
    if (!strstr((const char *)glGetString(GL_RENDERER),"D3D12")) return 2;
    const char *source = "#version 430\nlayout(local_size_x=64) in; layout(std430,binding=0) buffer Data { uint v[]; }; void main(){ uint i=gl_GlobalInvocationID.x; v[i]+=1u; }";
    GLuint shader = glCreateShader(GL_COMPUTE_SHADER);
    glShaderSource(shader,1,&source,NULL); glCompileShader(shader);
    GLint ok; glGetShaderiv(shader,GL_COMPILE_STATUS,&ok); if (!ok) return 3;
    GLuint program = glCreateProgram(); glAttachShader(program,shader); glLinkProgram(program);
    glGetProgramiv(program,GL_LINK_STATUS,&ok); if (!ok) return 3;
    glUseProgram(program);
    GLuint buffer; glGenBuffers(1,&buffer); glBindBuffer(GL_SHADER_STORAGE_BUFFER,buffer);
    glBufferData(GL_SHADER_STORAGE_BUFFER,1048576,NULL,GL_DYNAMIC_DRAW);
    glClearBufferSubData(GL_SHADER_STORAGE_BUFFER,GL_R32UI,0,1048576,GL_RED_INTEGER,GL_UNSIGNED_INT,NULL);
    uint32_t values[256]; for (unsigned i=0;i<256;i++) values[i]=i;
    glBufferSubData(GL_SHADER_STORAGE_BUFFER,0,sizeof(values),values);
    glBindBufferBase(GL_SHADER_STORAGE_BUFFER,0,buffer); glDispatchCompute(4,1,1);
    glMemoryBarrier(GL_BUFFER_UPDATE_BARRIER_BIT); glGetBufferSubData(GL_SHADER_STORAGE_BUFFER,0,sizeof(values),values);
    for (unsigned i=0;i<256;i++) if (values[i]!=i+1) return 3;
    glFinish();
    GLenum error = glGetError(); fprintf(stderr,"REPRO compute verified=256 gl_error=0x%x\n",error);
    if (error) return 3;
    if (remove) {
        int result = remove_own_device(); if (result) return result;
        /* 監視の経路と、確保失敗を NULL のまま上へ返す直前の経路を確認する。*/
        const char *wait_env = getenv("YOLUPAINTER_GPU_REPRO_WAIT_MS");
        unsigned wait_ms = wait_env ? (unsigned)atoi(wait_env) : 250;
        if (wait_ms > 2000) return 2;
        usleep(wait_ms * 1000);
        glBufferData(GL_SHADER_STORAGE_BUFFER,2097152,NULL,GL_DYNAMIC_DRAW);
        fprintf(stderr,"REPRO after-remove gl_error=0x%x reset=0x%x\n",glGetError(),glGetGraphicsResetStatusARB());
    }
    glDeleteBuffers(1,&buffer); glDeleteProgram(program); glDeleteShader(shader);
    glXMakeCurrent(display,None,NULL); glXDestroyContext(display,context);
    XDestroyWindow(display,window); XCloseDisplay(display); XFree(configs); XFree(visual);
    return 0;
}
