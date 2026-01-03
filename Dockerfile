FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

COPY . ./

RUN dotnet publish AurBuild -c Release -o _out


FROM archlinux:multilib-devel

LABEL org.opencontainers.image.source=https://github.com/na1307/AUR
LABEL org.opencontainers.image.description="AUR Build"
LABEL org.opencontainers.image.licenses=MIT

RUN echo "[bluehill]" >> /etc/pacman.conf

RUN echo "Server = https://storage.googleapis.com/bluehillaur/" >> /etc/pacman.conf

RUN pacman-key --init

RUN pacman-key -r AB69CFD0BE72421F --keyserver keys.openpgp.org

RUN pacman-key --lsign-key AB69CFD0BE72421F

RUN pacman -Syu --noconfirm dotnet-runtime-10.0 expect git

RUN useradd -ms /bin/bash builduser

RUN echo "builduser ALL=(ALL) NOPASSWD:ALL" >> /etc/sudoers

USER builduser

WORKDIR /home/builduser

RUN mkdir -p ./.config/pacman

RUN echo "BUILDENV=(!distcc color !ccache check sign)" >> ./.config/pacman/makepkg.conf

RUN echo "OPTIONS=(strip docs !libtool !staticlibs emptydirs zipman purge !debug lto)" >> ./.config/pacman/makepkg.conf

RUN echo "PACKAGER=\"Jonghyo Lee <na1307@outlook.kr>\"" >> ./.config/pacman/makepkg.conf

RUN echo "GPGKEY=\"AB69CFD0BE72421F\"" >> ./.config/pacman/makepkg.conf

COPY --from=build /_out .

COPY --from=build /build.sh .

RUN mkdir buildoutput

RUN sudo chmod +x ./build.sh

ENTRYPOINT ["./build.sh"]
