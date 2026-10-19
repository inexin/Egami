/* Prints the module grids zint makes, one JSON object per line, for tools/make-barcode-references.py.
 *
 * Input, one case per line on stdin, tab-separated: name, symbology, option_1, option_2, option_3, input_mode, data as hex.
 * Output: {"name": ..., "error": code, "message": ..., "width": w, "rows": r, "text": hrt, "modules": ["0101...", ...]}.
 * Uses only zint's public API (zint.h); a module is dark when bit (x & 7) of encoded_data[y][x >> 3] is set. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <zint.h>

static void put_json_string(const char *s)
{
    putchar('"');
    for (; *s; s++) {
        unsigned char c = (unsigned char)*s;
        if (c == '"' || c == '\\') {
            printf("\\%c", c);
        } else if (c < 0x20) {
            printf("\\u%04x", c);
        } else {
            putchar(c);
        }
    }
    putchar('"');
}

int main(void)
{
    static char line[65536];
    while (fgets(line, sizeof line, stdin)) {
        char *fields[7];
        int count = 0;
        char *p = strtok(line, "\t\r\n");
        while (p && count < 7) {
            fields[count++] = p;
            p = strtok(NULL, "\t\r\n");
        }
        if (count < 6) {
            continue;
        }

        static unsigned char data[32768];
        int length = 0;
        if (count == 7) {
            for (const char *h = fields[6]; h[0] && h[1]; h += 2) {
                unsigned int byte;
                sscanf(h, "%2x", &byte);
                data[length++] = (unsigned char)byte;
            }
        }

        struct zint_symbol *symbol = ZBarcode_Create();
        symbol->symbology = atoi(fields[1]);
        symbol->option_1 = atoi(fields[2]);
        symbol->option_2 = atoi(fields[3]);
        symbol->option_3 = atoi(fields[4]);
        symbol->input_mode = atoi(fields[5]);
        int error = ZBarcode_Encode(symbol, data, length);

        printf("{\"name\": ");
        put_json_string(fields[0]);
        printf(", \"error\": %d, \"message\": ", error);
        put_json_string(symbol->errtxt);
        printf(", \"width\": %d, \"rows\": %d, \"text\": ", symbol->width, symbol->rows);
        put_json_string((const char *)symbol->text);
        printf(", \"modules\": [");
        for (int y = 0; error < ZINT_ERROR && y < symbol->rows; y++) {
            printf(y ? ", \"" : "\"");
            for (int x = 0; x < symbol->width; x++) {
                putchar(((symbol->encoded_data[y][x >> 3] >> (x & 7)) & 1) ? '1' : '0');
            }
            putchar('"');
        }
        printf("]}\n");
        fflush(stdout);
        ZBarcode_Delete(symbol);
    }
    return 0;
}
