CXX ?= g++
CXXFLAGS ?= -std=c++17 -O2 -Wall -Wextra -pthread
LDLIBS ?=

ifeq ($(OS),Windows_NT)
LDLIBS += -lws2_32
endif

SRC := $(wildcard src/*.cpp)
TARGET := cpp-device-demo

all: $(TARGET)

$(TARGET): $(SRC)
	$(CXX) $(CXXFLAGS) $(SRC) -o $@ $(LDLIBS)

clean:
	rm -f $(TARGET) $(TARGET).exe

.PHONY: all clean
