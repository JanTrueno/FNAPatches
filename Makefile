PROJECTS:=$(wildcard */Makefile.inc)
PROJECT_DIRS:=$(PROJECTS:%/Makefile.inc=%)

all: $(PROJECTS) MMLoader EVBUnpack libs

# Suppress warnings when using the "clean" target.
ifneq ($(MAKECMDGOALS), clean)
$(MAKECMDGOALS):
	make -f Makefile.common $(MAKECMDGOALS)
endif

libs:
	make -C libs

MMLoader:
	make -f Makefile.common MMLoader

EVBUnpack:
	make -f Makefile.common EVBUnpack

%/Makefile.inc: %
	make -f Makefile.common $<

clean:
	@rm -rf out

.PHONY: all $(PROJECT_DIRS) $(MAKECMDGOALS) libs EVBUnpack