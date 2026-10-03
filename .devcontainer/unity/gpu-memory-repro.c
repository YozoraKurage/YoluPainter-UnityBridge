/* Unity を使わない GLX の確保／読み戻し／解放の再現。同期点で親が D3DKMT を測る。 */
#define GL_GLEXT_PROTOTYPES
#include <GL/gl.h>
#include <GL/glx.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static void stage(const char *name, int iteration)
{
    printf("{\"pid\":%d,\"stage\":\"%s\",\"iteration\":%d}\n", getpid(), name, iteration);
    fflush(stdout);
    if (getchar() == EOF) exit(2);
}

static void verify(void)
{
    GLenum error = glGetError();
    if (error) { fprintf(stderr, "OpenGL エラー: 0x%x\n", error); exit(3); }
}

int main(int argc, char **argv)
{
    if (argc != 6) return 2;
    int size = atoi(argv[1]), objects = atoi(argv[2]), iterations = atoi(argv[3]);
    int mapping = atoi(argv[4]), finish_each = atoi(argv[5]);
    XInitThreads();
    Display *display = XOpenDisplay(NULL);
    if (!display) { fprintf(stderr, "X ディスプレイを開けません\n"); return 2; }
    int attributes[] = {GLX_RGBA, GLX_RED_SIZE, 8, GLX_GREEN_SIZE, 8, GLX_BLUE_SIZE, 8, None};
    XVisualInfo *visual = glXChooseVisual(display, DefaultScreen(display), attributes);
    if (!visual) return 2;
    Colormap colormap = XCreateColormap(display, RootWindow(display, visual->screen), visual->visual, AllocNone);
    XSetWindowAttributes window_attributes = {.colormap = colormap};
    Window window = XCreateWindow(display, RootWindow(display, visual->screen), 0, 0, 16, 16, 0,
        visual->depth, InputOutput, visual->visual, CWColormap, &window_attributes);
    GLXContext context = glXCreateContext(display, visual, NULL, True);
    if (!context || !glXMakeCurrent(display, window, context)) return 2;
    fprintf(stderr, "renderer=%s version=%s\n", glGetString(GL_RENDERER), glGetString(GL_VERSION));
    if (!strstr((const char *)glGetString(GL_RENDERER), "D3D12")) {
        fprintf(stderr, "D3D12 の再現には実 GPU が必要です\n"); return 2;
    }
    GLuint *textures = calloc(objects, sizeof(GLuint));
    GLuint *buffers = calloc(objects, sizeof(GLuint));
    GLuint *frames = calloc(objects, sizeof(GLuint));
    if (!textures || !buffers || !frames) return 2;
    GLsizeiptr bytes = (GLsizeiptr)size * size * 4;
    stage("context_created", 0);
    for (int iteration = 1; iteration <= iterations; ++iteration) {
        glGenTextures(objects, textures);
        glGenBuffers(objects, buffers);
        glGenFramebuffers(objects, frames);
        for (int object = 0; object < objects; ++object) {
            glBindTexture(GL_TEXTURE_2D, textures[object]);
            glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, size, size, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
            glBindFramebuffer(GL_FRAMEBUFFER, frames[object]);
            glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, textures[object], 0);
            if (glCheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE) return 3;
            glViewport(0, 0, size, size);
            glClearColor(0.25f, 0.5f, 0.75f, 1.0f);
            glClear(GL_COLOR_BUFFER_BIT);
            glBindBuffer(GL_PIXEL_PACK_BUFFER, buffers[object]);
            glBufferData(GL_PIXEL_PACK_BUFFER, bytes, NULL, GL_STREAM_READ);
            glReadPixels(0, 0, size, size, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
            if (mapping) {
                const unsigned char *pixel = glMapBuffer(GL_PIXEL_PACK_BUFFER, GL_READ_ONLY);
                if (!pixel || abs(pixel[0] - 64) > 1 || abs(pixel[1] - 128) > 1 || abs(pixel[2] - 191) > 1 || pixel[3] != 255) {
                    if (pixel) fprintf(stderr, "読み戻した画素: %u,%u,%u,%u\n", pixel[0], pixel[1], pixel[2], pixel[3]);
                    fprintf(stderr, "読み戻した画素が一致しません\n"); return 3;
                }
                if (!glUnmapBuffer(GL_PIXEL_PACK_BUFFER)) return 3;
            }
        }
        verify();
        if (iteration == 1 || iteration == iterations) stage("allocated", iteration);
        glBindBuffer(GL_PIXEL_PACK_BUFFER, 0);
        glBindFramebuffer(GL_FRAMEBUFFER, 0);
        glBindTexture(GL_TEXTURE_2D, 0);
        glDeleteFramebuffers(objects, frames);
        glDeleteBuffers(objects, buffers);
        glDeleteTextures(objects, textures);
        if (iteration == iterations) stage("deleted_before_finish", iteration);
        if (finish_each || iteration == iterations) glFinish();
        verify();
        if (iteration % 4 == 0 || iteration == iterations)
            stage(finish_each || iteration == iterations ? "after_finish" : "after_delete", iteration);
    }
    sleep(3);
    stage("idle_3s", iterations);
    GLuint tiny;
    glGenBuffers(1, &tiny);
    glBindBuffer(GL_ARRAY_BUFFER, tiny);
    glBufferData(GL_ARRAY_BUFFER, 65536, NULL, GL_STREAM_DRAW);
    glDeleteBuffers(1, &tiny);
    glFinish();
    stage("small_allocation_after_idle", iterations);
    /* アップロードとは別の読み戻しキャッシュも、期限後に再び使う。 */
    GLuint small_texture, small_frame, small_buffer;
    glGenTextures(1, &small_texture);
    glBindTexture(GL_TEXTURE_2D, small_texture);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, 16, 16, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    glGenFramebuffers(1, &small_frame);
    glBindFramebuffer(GL_FRAMEBUFFER, small_frame);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, small_texture, 0);
    glClear(GL_COLOR_BUFFER_BIT);
    glGenBuffers(1, &small_buffer);
    glBindBuffer(GL_PIXEL_PACK_BUFFER, small_buffer);
    glBufferData(GL_PIXEL_PACK_BUFFER, 16 * 16 * 4, NULL, GL_STREAM_READ);
    glReadPixels(0, 0, 16, 16, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    const unsigned char *small_pixel = glMapBuffer(GL_PIXEL_PACK_BUFFER, GL_READ_ONLY);
    if (!small_pixel || abs(small_pixel[0] - 64) > 1 || small_pixel[3] != 255) return 3;
    if (!glUnmapBuffer(GL_PIXEL_PACK_BUFFER)) return 3;
    glBindBuffer(GL_PIXEL_PACK_BUFFER, 0);
    glBindTexture(GL_TEXTURE_2D, 0);
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    glDeleteBuffers(1, &small_buffer);
    glDeleteFramebuffers(1, &small_frame);
    glDeleteTextures(1, &small_texture);
    glFinish();
    verify();
    stage("small_readback_after_idle", iterations);
    free(textures); free(buffers); free(frames);
    glXMakeCurrent(display, None, NULL);
    glXDestroyContext(display, context);
    XDestroyWindow(display, window);
    XFreeColormap(display, colormap);
    XFree(visual);
    XCloseDisplay(display);
    stage("context_destroyed", iterations);
    return 0;
}
