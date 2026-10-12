/* Prints the script and direction HarfBuzz guesses for each input line (hb_buffer_guess_segment_properties), so the
   reference files record the run properties hb-shape used. */
#include <hb.h>
#include <stdio.h>
#include <string.h>

int main(void)
{
    static char line[1 << 20];
    while (fgets(line, sizeof line, stdin)) {
        size_t n = strlen(line);
        if (n && line[n - 1] == '\n')
            line[--n] = 0;
        hb_buffer_t *buffer = hb_buffer_create();
        hb_buffer_add_utf8(buffer, line, (int) n, 0, (int) n);
        hb_buffer_guess_segment_properties(buffer);
        char tag[5] = {0};
        hb_tag_to_string(hb_script_to_iso15924_tag(hb_buffer_get_script(buffer)), tag);
        printf("%s %s\n", tag, hb_direction_to_string(hb_buffer_get_direction(buffer)));
        hb_buffer_destroy(buffer);
    }
    return 0;
}
